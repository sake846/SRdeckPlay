using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sdk;

/// <summary>
/// Template for plugins whose public module is primarily lifecycle wiring.
/// The base class owns host validation, cancellation checks, serialized state
/// transitions, and idempotent disposal; protocol and presentation code stays
/// in the plugin-specific hooks.
/// </summary>
public abstract class PluginModuleBase : IPluginModule
{
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly List<Action> streamResetActions = [];
    private readonly TaskCompletionSource disposalCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int activationCleanupPending;
    private int disposed;
    private int state = (int)PluginLifecycleState.Discovered;
    private int streamCleanupPending;

    public abstract PluginDescriptor Descriptor { get; }

    public PluginLifecycleState State =>
        (PluginLifecycleState)Volatile.Read(ref state);

    /// <summary>
    /// The host context supplied during initialization, or <see langword="null"/>
    /// after disposal.
    /// </summary>
    protected IPluginHostContext? HostContext { get; private set; }

    protected bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public async ValueTask InitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostContext);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!string.Equals(hostContext.PluginId, Descriptor.Id, StringComparison.Ordinal))
                throw new ArgumentException("The host context belongs to a different plugin.", nameof(hostContext));

            HostContext = hostContext;
            await OnInitializeAsync(hostContext, cancellationToken).ConfigureAwait(false);
            SetState(PluginLifecycleState.Initialized);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (HostContext is null)
                throw new InvalidOperationException("The plugin is not initialized.");
            if (State != PluginLifecycleState.Initialized)
                throw new InvalidOperationException("The plugin must be initialized before activation.");

            Volatile.Write(ref activationCleanupPending, 1);
            try
            {
                await OnActivateAsync(cancellationToken).ConfigureAwait(false);
                SetState(PluginLifecycleState.Active);
                await OnActivatedAsync().ConfigureAwait(false);
            }
            catch (Exception activationException)
            {
                SetState(PluginLifecycleState.Initialized);
                try
                {
                    await RunDeactivationCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Plugin activation and its cleanup both failed.",
                        activationException,
                        cleanupException);
                }
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StartStreamAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State != PluginLifecycleState.Active)
                throw new InvalidOperationException("The plugin must be active before streaming starts.");

            Volatile.Write(ref streamCleanupPending, 1);
            try
            {
                HostContext?.Audio.Reset();
                ResetRegisteredStreamState();
                await OnStartStreamAsync(cancellationToken).ConfigureAwait(false);
                SetState(PluginLifecycleState.Streaming);
                await OnStreamStartedAsync().ConfigureAwait(false);
            }
            catch (Exception startException)
            {
                SetState(PluginLifecycleState.Active);
                try
                {
                    await RunStreamCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "Plugin stream start and its cleanup both failed.",
                        startException,
                        cleanupException);
                }
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StopStreamAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed) return;
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDisposed ||
                (State != PluginLifecycleState.Streaming &&
                 Volatile.Read(ref streamCleanupPending) == 0)) return;

            // Reject new IQ blocks before plugin cleanup waits for its processing
            // gate. Consumers must recheck State after entering that gate.
            SetState(PluginLifecycleState.Active);
            await RunStreamCleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DeactivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed) return;
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDisposed) return;
            PluginLifecycleState currentState = State;
            bool streamCleanupRequired = Volatile.Read(ref streamCleanupPending) != 0;
            bool activationCleanupRequired = Volatile.Read(ref activationCleanupPending) != 0;
            if (currentState is not (PluginLifecycleState.Active or PluginLifecycleState.Streaming) &&
                !streamCleanupRequired &&
                !activationCleanupRequired)
                return;

            if (currentState == PluginLifecycleState.Streaming || streamCleanupRequired)
            {
                SetState(PluginLifecycleState.Active);
                await RunStreamCleanupAsync(cancellationToken).ConfigureAwait(false);
            }

            SetState(PluginLifecycleState.Initialized);
            await RunDeactivationCleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            await disposalCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Exception? cleanupFailure = null;
                if (State == PluginLifecycleState.Streaming ||
                    Volatile.Read(ref streamCleanupPending) != 0)
                {
                    SetState(PluginLifecycleState.Active);
                    try
                    {
                        await RunStreamCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = Combine(cleanupFailure, exception);
                    }
                }

                if (State == PluginLifecycleState.Active ||
                    Volatile.Read(ref activationCleanupPending) != 0)
                {
                    SetState(PluginLifecycleState.Initialized);
                    try
                    {
                        await RunDeactivationCleanupAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = Combine(cleanupFailure, exception);
                    }
                }

                SetState(PluginLifecycleState.Disposed);
                try
                {
                    await OnDisposeAsync(HostContext).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = Combine(cleanupFailure, exception);
                }
                try
                {
                    HostContext?.Audio.Reset();
                }
                catch (Exception exception)
                {
                    cleanupFailure = Combine(cleanupFailure, exception);
                }
                HostContext = null;
                if (cleanupFailure is not null) throw cleanupFailure;
            }
            finally
            {
                lifecycleGate.Release();
            }
            disposalCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            HostContext = null;
            disposalCompletion.TrySetException(exception);
            throw;
        }
    }

    protected virtual ValueTask OnInitializeAsync(
        IPluginHostContext hostContext,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected virtual ValueTask OnActivateAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnActivatedAsync() => ValueTask.CompletedTask;

    protected virtual ValueTask OnStartStreamAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnStreamStartedAsync() => ValueTask.CompletedTask;

    protected virtual ValueTask OnStopStreamAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnStreamStoppedAsync() => ValueTask.CompletedTask;

    protected virtual ValueTask OnDeactivateAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnDeactivatedAsync() => ValueTask.CompletedTask;

    protected virtual ValueTask OnDisposeAsync(IPluginHostContext? hostContext) =>
        ValueTask.CompletedTask;

    /// <summary>
    /// Registers stream-scoped state that the common lifecycle resets before
    /// Start and after Stop. Registration must be completed by the module constructor.
    /// </summary>
    protected void RegisterStreamReset(Action reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        if (State != PluginLifecycleState.Discovered)
            throw new InvalidOperationException(
                "Stream reset actions must be registered before initialization.");
        streamResetActions.Add(reset);
    }

    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private async ValueTask RunStreamCleanupAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref streamCleanupPending) == 0) return;
        Exception? cleanupFailure = null;
        try
        {
            await OnStopStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        try
        {
            ResetRegisteredStreamState();
        }
        catch (Exception exception)
        {
            cleanupFailure = Combine(cleanupFailure, exception);
        }
        try
        {
            HostContext?.Audio.Reset();
        }
        catch (Exception exception)
        {
            cleanupFailure = Combine(cleanupFailure, exception);
        }
        if (cleanupFailure is not null) throw cleanupFailure;

        await OnStreamStoppedAsync().ConfigureAwait(false);
        Volatile.Write(ref streamCleanupPending, 0);
    }

    private async ValueTask RunDeactivationCleanupAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref activationCleanupPending) == 0) return;
        Exception? cleanupFailure = null;
        try
        {
            await OnDeactivateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }
        try
        {
            HostContext?.Audio.Reset();
        }
        catch (Exception exception)
        {
            cleanupFailure = Combine(cleanupFailure, exception);
        }
        if (cleanupFailure is not null) throw cleanupFailure;

        await OnDeactivatedAsync().ConfigureAwait(false);
        Volatile.Write(ref activationCleanupPending, 0);
    }

    private void ResetRegisteredStreamState()
    {
        Exception? resetFailure = null;
        foreach (Action reset in streamResetActions)
        {
            try
            {
                reset();
            }
            catch (Exception exception)
            {
                resetFailure = Combine(resetFailure, exception);
            }
        }
        if (resetFailure is not null) throw resetFailure;
    }

    private static Exception Combine(Exception? existing, Exception next) =>
        existing switch
        {
            null => next,
            AggregateException aggregate => new AggregateException([.. aggregate.InnerExceptions, next]),
            _ => new AggregateException(existing, next)
        };

    private void SetState(PluginLifecycleState value) =>
        Volatile.Write(ref state, (int)value);
}
