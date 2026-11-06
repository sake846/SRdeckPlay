using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using SRdeck.Audio;
using SRdeckPlugin.Contracts;

namespace SRdeckPlay;

internal sealed record RecordingMetadata
{
    public string? PluginId { get; init; }
    public string? RecordingId { get; init; }
    public int SegmentNumber { get; init; }
    public long RecordingSampleStart { get; init; }
    public string? Profile { get; init; }
    public long CenterFrequencyHz { get; init; }
    public int BandwidthHz { get; init; }
    public int SampleRateHz { get; init; }
    public long SampleCount { get; init; }
    public bool QInverted { get; init; }
    public IqRecordingTimelinePoint[]? Timeline { get; init; }
}

internal sealed record RecordingSegment(string Path, long Start, long Count, RecordingMetadata? Metadata);
internal sealed record RecordingDiscontinuity(double Position, string Description);

internal sealed class Recording
{
    public required RecordingSegment[] Segments { get; init; }
    public required int SampleRate { get; init; }
    public long SampleCount => Segments[^1].Start + Segments[^1].Count;
    public double Duration => SampleCount / (double)SampleRate;
    public RecordingMetadata? Metadata => Segments[0].Metadata;
    public float? ManualCalibrationOffsetDb { get; set; }
    public RecordingDiscontinuity[] Discontinuities { get; private set; } = [];
    internal RecordingSpectrumCache? SpectrumCache { get; set; }
    public bool HasRecordedCalibration => Segments.All(s => s.Metadata?.Timeline is { Length: > 0 } points
        && points.All(p => p.CalibrationOffsetDb is not null));
    public bool HasCalibration => ManualCalibrationOffsetDb is not null || HasRecordedCalibration;
    public string PowerUnit => HasCalibration ? "dBm" : "参考dBm";

    public DateTimeOffset? UtcAt(double seconds)
    {
        long sample = Math.Clamp((long)Math.Round(seconds * SampleRate), 0, SampleCount);
        var segment = SegmentAt(sample);
        long relative = sample - segment.Start;
        var point = segment.Metadata?.Timeline?.LastOrDefault(p => p.SampleOffset <= relative);
        return point?.UtcTimestamp.AddSeconds((relative - point.SampleOffset) / (double)SampleRate);
    }

    public string FormatTime(double seconds, bool fullDate = false) => UtcAt(seconds) is { } utc
        ? utc.ToLocalTime().ToString(fullDate ? "yyyy-MM-dd HH:mm:ss.fff zzz" : "HH:mm:ss.fff") : "録音時刻なし";

    public float PowerOffsetAt(long sample)
        => ManualCalibrationOffsetDb ?? RecordedPowerOffsetAt(sample);

    internal float RecordedPowerOffsetAt(long sample)
    {
        sample = Math.Clamp(sample, 0, SampleCount);
        var segment = SegmentAt(sample);
        var point = segment.Metadata?.Timeline?.LastOrDefault(p => p.SampleOffset <= sample - segment.Start);
        // Match the receiver telemetry's explicit fallback; the UI labels this as uncalibrated reference dBm.
        return point?.CalibrationOffsetDb ?? -80f;
    }

    public long NextBoundary(long sample)
    {
        var segment = SegmentAt(sample);
        return segment.Metadata?.Timeline?.FirstOrDefault(p => segment.Start + p.SampleOffset > sample) is { } next
            ? segment.Start + next.SampleOffset : segment.Start + segment.Count;
    }

    public bool IsDiscontinuityAt(long sample) => Discontinuities.Any(d =>
        Math.Abs(d.Position * SampleRate - sample) < 0.5);

    private RecordingSegment SegmentAt(long sample) => Segments[Array.FindLastIndex(Segments, s => s.Start <= sample)];

    public static Recording Open(string path)
    {
        path = Path.GetFullPath(path);
        RecordingMetadata? selected = ReadMetadata(Path.ChangeExtension(path, ".json"));
        var paths = new List<(string Path, RecordingMetadata? Metadata)> { (path, selected) };
        if (selected is { PluginId: "analog", RecordingId.Length: > 0, SegmentNumber: > 0 })
        {
            paths.Clear();
            foreach (string sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.json"))
            {
                RecordingMetadata? metadata;
                try { metadata = ReadMetadata(sidecar); }
                catch (JsonException) { continue; }
                if (metadata?.PluginId != "analog" || metadata.RecordingId != selected.RecordingId) continue;
                string wav = Path.ChangeExtension(sidecar, ".wav");
                if (!File.Exists(wav)) continue; // A recorder publishes the WAV last.
                paths.Add((wav, metadata));
            }
            paths.Sort((a, b) => a.Metadata!.SegmentNumber.CompareTo(b.Metadata!.SegmentNumber));
        }

        var segments = new List<RecordingSegment>();
        int rate = 0;
        long start = 0;
        for (int index = 0; index < paths.Count; index++)
        {
            var item = paths[index];
            using var reader = new PcmWaveFileReader(item.Path);
            if (reader.WaveFormat is not { Channels: 2, BitsPerSample: 16 })
                throw new InvalidDataException("16 bit PCM・2チャンネルのIQ WAV（左=I、右=Q）を選択してください。");
            if (reader.TotalSamples <= 0)
                throw new InvalidDataException("IQ WAVのデータ長が不正、または空です。");
            if (rate == 0) rate = reader.WaveFormat.SampleRate;
            // shortcut: segments share one clock; resample each segment if mixed-rate recordings need support.
            if (rate < 48_000 || rate > 2_000_000 || reader.WaveFormat.SampleRate != rate)
                throw new InvalidDataException("48 kHz～2 MHzの同一サンプルレートの録音に対応しています。");
            if (item.Metadata is { } meta)
            {
                long previousOffset = -1;
                foreach (var point in meta.Timeline ?? [])
                {
                    if (point is null || point.SampleOffset <= previousOffset || point.SampleOffset >= reader.TotalSamples ||
                        point.SampleOffset < 0 || (previousOffset == -1 && point.SampleOffset != 0) ||
                        point.CalibrationOffsetDb is { } offsetDb && !float.IsFinite(offsetDb))
                        throw new InvalidDataException("録音タイムラインが不正です。");
                    previousOffset = point.SampleOffset;
                }
                if ((meta.SampleRateHz != 0 && meta.SampleRateHz != rate) ||
                    (meta.SampleCount != 0 && meta.SampleCount != reader.TotalSamples))
                    throw new InvalidDataException("JSONの録音情報とWAVの内容が一致しません。");
                if (index > 0)
                {
                    var previous = paths[index - 1].Metadata!;
                    if (meta.SegmentNumber <= previous.SegmentNumber ||
                        meta.RecordingSampleStart < previous.RecordingSampleStart + segments[^1].Count ||
                        meta.CenterFrequencyHz != previous.CenterFrequencyHz)
                        throw new InvalidDataException("分割録音の欠落、重複、または中心周波数の変更があります。");
                }
            }
            segments.Add(new(item.Path, start, reader.TotalSamples, item.Metadata));
            start = checked(start + reader.TotalSamples);
        }
        var recording = new Recording { Segments = segments.ToArray(), SampleRate = rate };
        recording.Discontinuities = recording.BuildDiscontinuities();
        return recording;
    }

    private RecordingDiscontinuity[] BuildDiscontinuities()
    {
        var result = new List<RecordingDiscontinuity>();
        for (int i = 0; i < Segments.Length; i++)
        {
            var segment = Segments[i];
            double start = segment.Start / (double)SampleRate;
            foreach (var point in segment.Metadata?.Timeline ?? [])
                if (point.IsDiscontinuity)
                {
                    double time = (segment.Start + point.SampleOffset) / (double)SampleRate;
                    result.Add(new(time, $"入力欠落・再開（欠落入力 {point.MissingInputSamples:N0}サンプル）"));
                }
            if (i == 0) continue;
            var previous = Segments[i - 1];
            if (segment.Metadata?.SegmentNumber > previous.Metadata?.SegmentNumber + 1 ||
                segment.Metadata?.RecordingSampleStart > previous.Metadata?.RecordingSampleStart + previous.Count)
                result.Add(new(start, "分割録音ファイルの欠落"));
            else if (!result.Any(d => d.Position == start) &&
                segment.Metadata?.Timeline?.FirstOrDefault()?.UtcTimestamp is { } utc &&
                UtcBeforeEnd(previous) is { } end && Math.Abs((utc - end).TotalSeconds) > 0.05)
                result.Add(new(start, $"録音時刻の不連続: {(utc - end).TotalSeconds:+0.000;-0.000}秒"));
        }
        return result.OrderBy(d => d.Position).ToArray();
    }

    private DateTimeOffset? UtcBeforeEnd(RecordingSegment segment) => segment.Metadata?.Timeline?.LastOrDefault() is { } point
        ? point.UtcTimestamp.AddSeconds((segment.Count - point.SampleOffset) / (double)SampleRate)
        : null;

    private static RecordingMetadata? ReadMetadata(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<RecordingMetadata>(File.ReadAllText(path)) : null;
}

internal sealed class RecordingReader(Recording recording) : IDisposable
{
    private PcmWaveFileReader? reader;
    private int segmentIndex = -1;
    private byte[] bytes = [];
    public long Position { get; private set; }

    public void Seek(long sample)
    {
        Position = Math.Clamp(sample, 0, recording.SampleCount);
        int index = Array.FindLastIndex(recording.Segments, s => s.Start <= Position);
        SelectSegment(index);
        reader!.Position = (Position - recording.Segments[index].Start) * 4;
    }

    public int Read(Span<Complex32> output)
    {
        if (reader is null) Seek(Position);
        int total = 0;
        if (bytes.Length < output.Length * 4) bytes = new byte[output.Length * 4];
        while (total < output.Length && Position < recording.SampleCount)
        {
            int wanted = (int)Math.Min(output.Length - total,
                recording.Segments[segmentIndex].Start + recording.Segments[segmentIndex].Count - Position);
            if (wanted == 0) { SelectSegment(segmentIndex + 1); continue; }
            int count = 0;
            while (count < wanted * 4)
            {
                int read = reader!.Read(bytes, count, wanted * 4 - count);
                if (read == 0) throw new EndOfStreamException("録音ファイルが途中で終了しました。");
                count += read;
            }
            float qSign = recording.Segments[segmentIndex].Metadata?.QInverted == true ? -1 : 1;
            for (int n = 0; n < wanted; n++)
                output[total + n] = new Complex32(
                    BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(n * 4)) / 32768f,
                    qSign * BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(n * 4 + 2)) / 32768f);
            total += wanted;
            Position += wanted;
        }
        return total;
    }

    private void SelectSegment(int index)
    {
        if (index == segmentIndex) return;
        reader?.Dispose();
        reader = new PcmWaveFileReader(recording.Segments[index].Path);
        segmentIndex = index;
    }

    public void Dispose() => reader?.Dispose();
}
