using System.Text.Json;
using System.Threading.Channels;

namespace SRdeckPlugin.Sdk;

/// <summary>
/// Retention policy supplied to <see cref="PluginJsonLinesHistoryWriter{T}"/>.
/// A value of zero disables the corresponding numeric limit; a null age means
/// that no time-based deletion is requested.
/// </summary>
public sealed record PluginJsonLinesHistoryPolicy(
    int MaximumEntries,
    TimeSpan? MaximumAge = null,
    long MaximumBytes = 0);

/// <summary>
/// Bounded, single-reader JSONL writer for high-rate plugin reception events.
/// The producer only copies a decoded record into an in-memory channel; all
/// filesystem work, batching and compaction happen on the worker task.
/// </summary>
public sealed class PluginJsonLinesHistoryWriter<T> : IAsyncDisposable
{
    private readonly string path;
    private readonly Func<PluginJsonLinesHistoryPolicy> policyProvider;
    private readonly Func<T, DateTimeOffset>? timestampSelector;
    private readonly JsonSerializerOptions? options;
    private readonly int batchSize;
    private readonly TimeSpan flushInterval;
    private readonly Channel<T> queue;
    private readonly Task worker;
    private int disposed;
    private long droppedCount;
    private long pendingCount;

    public PluginJsonLinesHistoryWriter(
        string path,
        Func<PluginJsonLinesHistoryPolicy> policyProvider,
        Func<T, DateTimeOffset>? timestampSelector = null,
        JsonSerializerOptions? options = null,
        int queueCapacity = 4096,
        int batchSize = 128,
        TimeSpan? flushInterval = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A history path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(policyProvider);
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (batchSize < 1) throw new ArgumentOutOfRangeException(nameof(batchSize));

        this.path = path;
        this.policyProvider = policyProvider;
        this.timestampSelector = timestampSelector;
        this.options = options;
        this.batchSize = batchSize;
        this.flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);
        if (this.flushInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(flushInterval));
        queue = Channel.CreateBounded<T>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(RunAsync);
    }

    public long DroppedCount => Interlocked.Read(ref droppedCount);

    /// <summary>Raised on the worker thread; exceptions never escape reception.</summary>
    public event Action<Exception>? SaveFailed;

    public bool TryEnqueue(T value)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        // Reserve the item before publishing it.  The worker may consume a
        // value immediately, so incrementing afterwards would let FlushAsync
        // observe a transient zero and return too early.
        Interlocked.Increment(ref pendingCount);
        if (queue.Writer.TryWrite(value))
            return true;
        Interlocked.Decrement(ref pendingCount);
        Interlocked.Increment(ref droppedCount);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        try { await worker.ConfigureAwait(false); }
        catch (Exception exception) { SaveFailed?.Invoke(exception); }
    }

    /// <summary>Waits until all records accepted by <see cref="TryEnqueue"/> are written.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref pendingCount) > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<T>(batchSize);
                if (!queue.Reader.TryRead(out T? first)) continue;
                batch.Add(first);

                // Give a short burst a chance to coalesce before opening the file.
                await Task.Delay(flushInterval).ConfigureAwait(false);
                while (batch.Count < batchSize && queue.Reader.TryRead(out T? value))
                    batch.Add(value);

                try
                {
                    PluginJsonLinesHistoryPolicy policy = policyProvider();
                    PluginJsonLinesHistory.AppendBatchAndRetain(
                        path, batch, policy.MaximumEntries, policy.MaximumAge,
                        DateTimeOffset.UtcNow, timestampSelector, options, policy.MaximumBytes);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    SaveFailed?.Invoke(exception);
                }
                finally
                {
                    Interlocked.Add(ref pendingCount, -batch.Count);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
