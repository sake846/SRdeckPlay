using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SRdeckPlugin.Sdk;

/// <summary>Append-only JSONL history with serialized reads, writes and bounded retention.</summary>
public static partial class PluginJsonLinesHistory
{
    private static readonly ConcurrentDictionary<string, HistoryFileState> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<T> Load<T>(string path, int maximumEntries,
        JsonSerializerOptions? options = null)
    {
        if (maximumEntries <= 0) return [];
        lock (StateFor(path))
        {
            return ReadValid<T>(path, options).TakeLast(maximumEntries).ToArray();
        }
    }

    /// <summary>Reads a consistent snapshot under the same gate as append and retention.</summary>
    public static IReadOnlyList<T> LoadAll<T>(string path, JsonSerializerOptions? options = null)
    {
        lock (StateFor(path)) return ReadValid<T>(path, options).ToArray();
    }

    public static void Append<T>(string path, T value, JsonSerializerOptions? options = null) =>
        AppendBatchAndRetain(path, [value], 0, null, DateTimeOffset.UtcNow, options: options);

    public static void AppendAndRetain<T>(string path, T value, int maximumEntries,
        TimeSpan? maximumAge, DateTimeOffset now, Func<T, DateTimeOffset>? timestampSelector = null,
        JsonSerializerOptions? options = null, long maximumBytes = 0) =>
        AppendBatchAndRetain(path, [value], maximumEntries, maximumAge, now, timestampSelector, options, maximumBytes);

    /// <summary>
    /// Serializes each new record once, then removes old records in a single streaming pass if needed.
    /// Numeric limits are enforced after each batch, including records larger than the byte limit.
    /// </summary>
    public static void AppendBatchAndRetain<T>(string path, IReadOnlyCollection<T> values,
        int maximumEntries, TimeSpan? maximumAge, DateTimeOffset now,
        Func<T, DateTimeOffset>? timestampSelector = null, JsonSerializerOptions? options = null,
        long maximumBytes = 0)
    {
        if (values.Count == 0) return;
        path = Path.GetFullPath(path);
        HistoryFileState state = StateFor(path);
        lock (state)
        {
            RefreshState(path, state);
            ValidateAgeCache(state, timestampSelector, options);
            string appendText = string.Join(Environment.NewLine,
                values.Select(value => JsonSerializer.Serialize(value, options))) + Environment.NewLine;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Keep a previously interrupted final line separate from the next complete record.
            if (state.Length > 0 && !EndsWithNewline(path)) appendText = Environment.NewLine + appendText;
            File.AppendAllText(path, appendText, Utf8);
            state.Count = checked(state.Count + values.Count);
            if (state.AgeKnown && timestampSelector is not null)
                foreach (T value in values)
                    state.Earliest = Earlier(state.Earliest, timestampSelector(value));
            RememberFile(path, state);
            RetainIfNeeded(path, state, maximumEntries, maximumAge, now, timestampSelector, options, maximumBytes);
        }
    }

    /// <summary>Applies retention without adding a record, sharing the writer's per-file gate.</summary>
    public static void ApplyRetention<T>(string path, int maximumEntries, TimeSpan? maximumAge,
        DateTimeOffset now, Func<T, DateTimeOffset>? timestampSelector = null,
        JsonSerializerOptions? options = null, long maximumBytes = 0)
    {
        path = Path.GetFullPath(path);
        HistoryFileState state = StateFor(path);
        lock (state)
        {
            RefreshState(path, state);
            ValidateAgeCache(state, timestampSelector, options);
            RetainIfNeeded(path, state, maximumEntries, maximumAge, now, timestampSelector, options, maximumBytes);
        }
    }

    public static void Delete(string path)
    {
        HistoryFileState state = StateFor(path);
        lock (state)
        {
            if (File.Exists(path)) File.Delete(path);
            state.Count = -1;
            state.AgeKnown = false;
        }
    }

    public static void Rewrite<T>(string path, IEnumerable<T> values, JsonSerializerOptions? options = null)
    {
        path = Path.GetFullPath(path);
        HistoryFileState state = StateFor(path);
        lock (state)
        {
            state.Count = RewriteLines(path, values.Select(value => JsonSerializer.Serialize(value, options)));
            state.AgeKnown = false;
            RememberFile(path, state);
        }
    }

    private static IEnumerable<T> ReadValid<T>(string path, JsonSerializerOptions? options)
    {
        if (!File.Exists(path)) yield break;
        foreach (string line in File.ReadLines(path))
            if (TryRead(line, options, out T? value)) yield return value!;
    }

    private static bool TryRead<T>(string line, JsonSerializerOptions? options, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try { value = JsonSerializer.Deserialize<T>(line, options); return value is not null; }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static int RewriteLines(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            int count = 0;
            using (var writer = new StreamWriter(temporaryPath, append: false, Utf8))
                foreach (string line in lines) { writer.WriteLine(line); count++; }
            File.Move(temporaryPath, path, overwrite: true);
            return count;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool EndsWithNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() is 10 or 13;
    }

    private static HistoryFileState StateFor(string path) =>
        Files.GetOrAdd(Path.GetFullPath(path), static _ => new HistoryFileState());
}
