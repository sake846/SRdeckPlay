using System.Text.Json;

namespace SRdeckPlugin.Sdk;

public static partial class PluginJsonLinesHistory
{
    private sealed class HistoryFileState
    {
        public int Count = -1;
        public long Length;
        public DateTime LastWriteUtc;
        public bool AgeKnown;
        public DateTimeOffset? Earliest;
        public Delegate? TimestampSelector;
        public JsonSerializerOptions? Options;
    }

    private readonly record struct RetainedLine(string Text, long Bytes, DateTimeOffset? Timestamp);

    private static void RetainIfNeeded<T>(string path, HistoryFileState state, int maximumEntries,
        TimeSpan? maximumAge, DateTimeOffset now, Func<T, DateTimeOffset>? timestampSelector,
        JsonSerializerOptions? options, long maximumBytes)
    {
        DateTimeOffset? cutoff = maximumAge is not null && timestampSelector is not null
            ? now - maximumAge.Value : null;
        bool ageDue = cutoff is not null && (!state.AgeKnown || state.Earliest < cutoff);
        if (!ageDue && !(maximumEntries > 0 && state.Count > maximumEntries) &&
            !(maximumBytes > 0 && state.Length > maximumBytes)) return;

        // Retain raw JSON lines: custom converters are not repeatedly invoked and
        // forward-compatible fields survive compaction. The queue is bounded by policy.
        var retained = new Queue<RetainedLine>();
        long retainedBytes = 0;
        // Canonicalize an over-budget file even when its excess is only a BOM
        // or a different newline encoding, rather than leaving it over the cap.
        bool changed = maximumBytes > 0 && state.Length > maximumBytes;
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path))
            {
                if (!TryRead(line, options, out T? value)) { changed = true; continue; }
                DateTimeOffset? timestamp = timestampSelector?.Invoke(value!);
                if (timestamp < cutoff) { changed = true; continue; }
                long bytes = Utf8.GetByteCount(line) + Environment.NewLine.Length;
                retained.Enqueue(new(line, bytes, timestamp));
                retainedBytes += bytes;
                while ((maximumEntries > 0 && retained.Count > maximumEntries) ||
                       (maximumBytes > 0 && retainedBytes > maximumBytes))
                {
                    retainedBytes -= retained.Dequeue().Bytes;
                    changed = true;
                }
            }
        }

        if (changed) RewriteLines(path, retained.Select(entry => entry.Text));
        state.Count = retained.Count;
        state.Earliest = retained.Min(entry => entry.Timestamp);
        state.AgeKnown = timestampSelector is not null;
        RememberFile(path, state);
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;

    private static void ValidateAgeCache<T>(HistoryFileState state,
        Func<T, DateTimeOffset>? timestampSelector, JsonSerializerOptions? options)
    {
        if (!Equals(state.TimestampSelector, timestampSelector) || !ReferenceEquals(state.Options, options))
            state.AgeKnown = false;
        state.TimestampSelector = timestampSelector;
        state.Options = options;
    }

    private static void RefreshState(string path, HistoryFileState state)
    {
        var file = new FileInfo(path);
        long length = file.Exists ? file.Length : 0;
        DateTime lastWrite = file.Exists ? file.LastWriteTimeUtc : default;
        if (state.Count >= 0 && state.Length == length && state.LastWriteUtc == lastWrite) return;
        state.Count = file.Exists ? File.ReadLines(path).Count(line => !string.IsNullOrWhiteSpace(line)) : 0;
        state.Length = length;
        state.LastWriteUtc = lastWrite;
        state.AgeKnown = false;
    }

    private static void RememberFile(string path, HistoryFileState state)
    {
        var file = new FileInfo(path);
        state.Length = file.Exists ? file.Length : 0;
        state.LastWriteUtc = file.Exists ? file.LastWriteTimeUtc : default;
    }
}
