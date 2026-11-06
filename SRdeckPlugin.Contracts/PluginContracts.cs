namespace SRdeckPlugin.Contracts;

/// <summary>
/// Capabilities supplied by an SDR plugin. Unknown flag values must be ignored
/// so that older hosts can still inspect newer plugins safely.
/// </summary>
[Flags]
public enum PluginCapabilities
{
    None = 0,
    IqConsumer = 1 << 0,
    MainView = 1 << 1,
    SettingsView = 1 << 2,
    FrequencyOverlay = 1 << 3,
    AudioProducer = 1 << 4,
    ResultPublisher = 1 << 5,
    Export = 1 << 6,
    Headless = 1 << 7,
    ChannelIqConsumer = 1 << 8,
    WaterfallAnnotation = 1 << 9,
    WaterfallDisplay = 1 << 10
}

public enum PluginLifecycleState
{
    Discovered,
    Initialized,
    Active,
    Streaming,
    Faulted,
    Disposed
}

/// <summary>
/// Indicates that activation cannot proceed with the current host configuration,
/// but that the plugin itself remains healthy and can be activated again later.
/// </summary>
public sealed class PluginActivationRejectedException(string message) : InvalidOperationException(message);

public enum PluginLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Immutable metadata that is available before an plugin is initialized.
/// </summary>
public sealed record PluginDescriptor(
    string Id,
    string DisplayName,
    string Description,
    Version PluginVersion,
    Version MinimumHostApiVersion,
    Version MaximumHostApiVersion,
    PluginCapabilities Capabilities,
    string Provider,
    string License,
    bool IsEnabledByDefault = false);

public static class PluginHostApi
{
    public static Version CurrentVersion { get; } = new(1, 0);
}

public interface IPluginLogger
{
    void Log(PluginLogLevel level, string eventId, string message, Exception? exception = null);
}

public interface IPluginDispatcher
{
    bool CheckAccess();
    void Post(Action action);
}

/// <summary>
/// Stable, plugin-owned operating mode metadata. Profile IDs are persisted
/// together with the plugin ID, so they must not be localized or recycled.
/// </summary>
public sealed record PluginProfileDescriptor(
    string Id,
    string DisplayName,
    string Description,
    bool IsDefault = false);

/// <summary>
/// Optional capability for plugins that expose selectable operating profiles.
/// Profile selection is separate from tuning: an plugin may translate a
/// selected profile into one or more tuning requests when it is activated.
/// </summary>
public interface IPluginProfileProvider
{
    IReadOnlyList<PluginProfileDescriptor> Profiles { get; }
    string? SelectedProfileId { get; }
    ValueTask SelectProfileAsync(string profileId, CancellationToken cancellationToken);
}

/// <summary>
/// Marks a profile provider that can safely retune and reset its processing
/// state while its IQ stream remains active.
/// </summary>
public interface ILivePluginProfileProvider : IPluginProfileProvider;

/// <summary>
/// The minimal host surface made available during the first implementation
/// phase. Additional services are added as small capability interfaces rather
/// than by exposing the host DI container.
/// </summary>
public interface IPluginHostContext
{
    string PluginId { get; }
    TimeProvider TimeProvider { get; }
    IPluginLogger Logger { get; }
    IPluginSettingsStore Settings { get; }
    IPluginTuningService Tuning { get; }
    IPluginAudioSink Audio { get; }
    IPluginDispatcher Dispatcher { get; }
    IPluginMetrics Metrics => NullPluginMetrics.Instance;
    IPluginRuntimeDiagnostics RuntimeDiagnostics => NullPluginRuntimeDiagnostics.Instance;
    IPluginNotificationService Notifications { get; }
    IPluginReceiverTelemetry? ReceiverTelemetry => null;
    IPluginSpectrumService Spectrum => NullPluginSpectrumService.Instance;
}

/// <summary>
/// Host-calibrated receiver telemetry shared by plugins that display RF level.
/// </summary>
public interface IPluginReceiverTelemetry
{
    float SignalLevelDbm { get; }
    float NoiseFloorDbm => -150f;
    float CalibrationOffsetDb => -80f;
    float DbfsToDbm(float dbfs) => float.IsFinite(dbfs) ? dbfs + CalibrationOffsetDb : float.NaN;
    float DbmToDbfs(float dbm) => float.IsFinite(dbm) ? dbm - CalibrationOffsetDb : float.NaN;
}

/// <summary>
/// Base lifecycle contract for all plugins. Implementations must make stop,
/// deactivate, and dispose operations safe to call more than once.
/// </summary>
public interface IPluginModule : IAsyncDisposable
{
    PluginDescriptor Descriptor { get; }
    PluginLifecycleState State { get; }

    ValueTask InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken);
    ValueTask ActivateAsync(CancellationToken cancellationToken);
    ValueTask StartStreamAsync(CancellationToken cancellationToken);
    ValueTask StopStreamAsync(CancellationToken cancellationToken);
    ValueTask DeactivateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability for moving lazy DSP initialization and tiered
/// optimization out of the first live IQ block. Implementations must not
/// publish decoded data or retain warm-up samples in user-visible history.
/// </summary>
public interface IPluginProcessingWarmup
{
    ValueTask WarmUpProcessingAsync(
        PluginProcessingWarmupContext context,
        CancellationToken cancellationToken);
}

public readonly record struct PluginProcessingWarmupContext(
    int SampleRateHz,
    long CenterFrequencyHz,
    int BlockCount = 3);

/// <summary>
/// Processes a synthetic channel-IQ block during application start-up.
/// The span is valid only for the duration of the callback.
/// </summary>
public delegate void PluginChannelWarmupProcessor(
    ReadOnlySpan<Complex32> samples,
    ChannelIqBlockMetadata metadata);

/// <summary>
/// Shared implementation for the standard channel-IQ warm-up sequence.
/// Plugins supply only their channel settings, processing callback, and
/// state-reset callback so every built-in receiver observes the same timing
/// and discontinuity semantics.
/// </summary>
public static class PluginProcessingWarmup
{
    public static ValueTask RunChannelAsync(
        PluginProcessingWarmupContext context,
        string requestId,
        long channelCenterFrequencyHz,
        int outputSampleRateHz,
        int bandwidthHz,
        PluginChannelWarmupProcessor process,
        Action reset,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bandwidthHz);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(reset);

        int blockCount = Math.Clamp(context.BlockCount, 1, 8);
        int inputSampleRateHz = Math.Max(outputSampleRateHz, context.SampleRateHz);
        int sampleCount = Math.Max(1, outputSampleRateHz / 10);
        return new ValueTask(Task.Run(() =>
        {
            Complex32[] samples = System.Buffers.ArrayPool<Complex32>.Shared.Rent(sampleCount);
            try
            {
                samples.AsSpan(0, sampleCount).Clear();
                Guid streamId = Guid.NewGuid();
                var configuration = new AppliedChannelConfiguration(
                    requestId,
                    channelCenterFrequencyHz,
                    context.CenterFrequencyHz,
                    inputSampleRateHz,
                    outputSampleRateHz,
                    bandwidthHz,
                    1,
                    1,
                    1,
                    1,
                    33,
                    2,
                    0,
                    "startup-warm-up");
                for (int block = 0; block < blockCount; block++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long sourceStart = (long)block * inputSampleRateHz / 10;
                    var source = new IqBlockMetadata(
                        streamId,
                        0,
                        block,
                        sourceStart,
                        System.Diagnostics.Stopwatch.GetTimestamp(),
                        DateTimeOffset.UnixEpoch.AddTicks(
                            block * TimeSpan.TicksPerSecond / 10),
                        inputSampleRateHz,
                        context.CenterFrequencyHz,
                        inputSampleRateHz / 10,
                        IqInputSource.Playback,
                        block == 0
                            ? IqDiscontinuity.StreamStarted
                            : IqDiscontinuity.None);
                    var metadata = new ChannelIqBlockMetadata(
                        source,
                        (long)block * sampleCount,
                        0,
                        sampleCount,
                        configuration);
                    process(samples.AsSpan(0, sampleCount), metadata);
                }
            }
            finally
            {
                try
                {
                    reset();
                }
                finally
                {
                    System.Buffers.ArrayPool<Complex32>.Shared.Return(samples);
                }
            }
        }, cancellationToken));
    }
}
