using System.IO;
using System.Text;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Contracts;
using SRdeck.Renderers;
using SRdeck.Models;

namespace SRdeckPlay;

internal sealed class SpectrumAnalyzer
{
    public const int Size = AppConstants.FFT_SIZE;
    public const int Bins = Size;
    private readonly int sampleCount;
    private readonly Radix2Fft fft;
    private readonly float[] real, imaginary, window;
    private Complex32[]? history;

    public SpectrumAnalyzer(int sampleCount = Size)
    {
        if (sampleCount <= 0 || (sampleCount & (sampleCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        this.sampleCount = sampleCount;
        fft = new(sampleCount);
        real = new float[sampleCount];
        imaginary = new float[sampleCount];
        window = Enumerable.Range(0, sampleCount)
            .Select(i => 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (sampleCount - 1))).ToArray();
    }

    public float[] PushAndAnalyze(ReadOnlySpan<Complex32> input)
    {
        history ??= new Complex32[sampleCount];
        if (input.Length >= sampleCount) input[^sampleCount..].CopyTo(history);
        else
        {
            history.AsSpan(input.Length).CopyTo(history);
            input.CopyTo(history.AsSpan(sampleCount - input.Length));
        }
        return Analyze(history);
    }

    public float[] Analyze(ReadOnlySpan<Complex32> input)
        => Analyze(input, Bins);

    public float[] Analyze(ReadOnlySpan<Complex32> input, int bins)
    {
        if (bins <= 0 || sampleCount % bins != 0) throw new ArgumentOutOfRangeException(nameof(bins));
        var result = new float[bins];
        Analyze(input, result);
        return result;
    }

    public void Analyze(ReadOnlySpan<Complex32> input, Span<float> result)
    {
        if (result.IsEmpty || sampleCount % result.Length != 0)
            throw new ArgumentOutOfRangeException(nameof(result));
        int count = Math.Min(sampleCount, input.Length);
        Array.Clear(real, count, sampleCount - count);
        Array.Clear(imaginary, count, sampleCount - count);
        for (int i = 0; i < count; i++)
        {
            real[i] = input[i].I * window[i];
            imaginary[i] = input[i].Q * window[i];
        }
        fft.Transform(real, imaginary);
        float normalization = 4 / ((float)sampleCount * sampleCount);
        int samplesPerBin = sampleCount / result.Length;
        int shift = result.Length == 1 ? 0 : sampleCount / 2;
        // Logarithms preserve peak order, so convert only the aggregated bins.
        for (int bin = 0; bin < result.Length; bin++)
        {
            int first = (bin * samplesPerBin + shift) % sampleCount;
            float peak = 0;
            for (int i = first; i < first + samplesPerBin; i++)
                peak = Math.Max(peak, real[i] * real[i] + imaginary[i] * imaginary[i]);
            result[bin] = 10 * MathF.Log10(Math.Max(peak * normalization, 1e-12f));
        }
    }
}

internal sealed record OverviewData(double Start, double End, float[] Rssi, float[] Spectra);

internal sealed class CachedSpectrumSegment(long start, long sampleCount, int bucketSamples, byte[] levels,
    byte[] rssiLevels)
{
    public long Start { get; } = start;
    public long SampleCount { get; } = sampleCount;
    public int BucketSamples { get; } = bucketSamples;
    public int BucketCount => levels.Length / SignalAnalysis.Bins;
    public ReadOnlySpan<byte> Bucket(int index) => levels.AsSpan(index * SignalAnalysis.Bins, SignalAnalysis.Bins);
    public byte RssiBucket(int index) => rssiLevels[index];
}

internal readonly record struct SpectrumFrameSpec(long Start, int Length);
internal readonly record struct CacheSourceDependency(string Path, long Length, long LastWriteTicks, long SampleCount, bool QInverted);
internal readonly record struct CacheMetadataDependency(long Length, long LastWriteTicks);
internal sealed record RecordingSpectrumCache(CachedSpectrumSegment[] Segments);

internal static class SignalAnalysis
{
    public const int Bins = 256;
    private const int HopMilliseconds = 100;
    private const int BucketMilliseconds = 1000;
    private const int CacheLevelMinimum = -240;
    private const int CacheVersion = 10;

    public static RecordingSpectrumCache LoadOrCreateCache(Recording recording, CancellationToken cancellation,
        IProgress<string>? progress = null)
    {
        var segments = new CachedSpectrumSegment[recording.Segments.Length];
        int fftSampleCount = GetFftSampleCount(recording.SampleRate);
        for (int i = 0; i < segments.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(Path.GetFileName(recording.Segments[i].Path));
            segments[i] = LoadOrCreateSegmentCache(recording, i, fftSampleCount, cancellation);
        }
        return new(segments);
    }

    private static int GetFftSampleCount(int sampleRate)
    {
        int minimum = checked((sampleRate * 15 + 99) / 100);
        int count = 1;
        while (count < minimum) count <<= 1;
        if ((long)count * 100 >= (long)sampleRate * 30)
            throw new InvalidOperationException("150～300 ms未満に収まるFFTサンプル数を決められません。");
        return count;
    }

    public static OverviewData Analyze(Recording recording, double start, double end, int columns,
        CancellationToken cancellation, IProgress<int>? progress = null)
    {
        cancellation.ThrowIfCancellationRequested();
        long first = Math.Clamp((long)(start * recording.SampleRate), 0, recording.SampleCount - 1);
        long last = Math.Clamp((long)(end * recording.SampleRate), first + 1, recording.SampleCount);
        columns = (int)Math.Min(columns, last - first);
        var levels = new float[columns];
        var spectra = new float[columns * Bins];
        var rssiPower = new double[columns];
        var rssiCounts = new long[columns];
        Array.Fill(spectra, float.NegativeInfinity);
        var cache = recording.SpectrumCache ??= LoadOrCreateCache(recording, cancellation);
        long visibleSamples = last - first;
        foreach (var segment in cache.Segments)
        {
            long overlapStart = Math.Max(first, segment.Start);
            long overlapEnd = Math.Min(last, segment.Start + segment.SampleCount);
            if (overlapStart >= overlapEnd) continue;
            int firstBucket = (int)((overlapStart - segment.Start) / segment.BucketSamples);
            int lastBucket = (int)((overlapEnd - 1 - segment.Start) / segment.BucketSamples);
            for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                cancellation.ThrowIfCancellationRequested();
                long bucketStart = segment.Start + (long)bucket * segment.BucketSamples;
                long bucketEnd = Math.Min(segment.Start + segment.SampleCount, bucketStart + segment.BucketSamples);
                long bucketVisibleStart = Math.Max(first, bucketStart);
                long bucketVisibleEnd = Math.Min(last, bucketEnd);
                int firstColumn = (int)((bucketVisibleStart - first) * columns / visibleSamples);
                int lastColumn = (int)((bucketVisibleEnd - 1 - first) * columns / visibleSamples);
                long center = bucketStart + (bucketEnd - bucketStart) / 2;
                float calibrationDelta = recording.PowerOffsetAt(center) - recording.RecordedPowerOffsetAt(center);
                ReadOnlySpan<byte> values = segment.Bucket(bucket);
                for (int column = firstColumn; column <= Math.Min(columns - 1, lastColumn); column++)
                    for (int bin = 0; bin < Bins; bin++)
                    {
                        float dbm = MathF.Round(values[bin] + CacheLevelMinimum + calibrationDelta);
                        int index = (Bins - 1 - bin) * columns + column;
                        spectra[index] = Math.Max(spectra[index], dbm);
                    }
            }
        }

        foreach (var segment in cache.Segments)
        {
            long overlapStart = Math.Max(first, segment.Start);
            long overlapEnd = Math.Min(last, segment.Start + segment.SampleCount);
            if (overlapStart >= overlapEnd) continue;
            int firstBucket = (int)((overlapStart - segment.Start) / segment.BucketSamples);
            int lastBucket = (int)((overlapEnd - 1 - segment.Start) / segment.BucketSamples);
            for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                long bucketStart = segment.Start + (long)bucket * segment.BucketSamples;
                long bucketEnd = Math.Min(segment.Start + segment.SampleCount, bucketStart + segment.BucketSamples);
                long bucketVisibleStart = Math.Max(first, bucketStart);
                long bucketVisibleEnd = Math.Min(last, bucketEnd);
                long bucketCenter = bucketStart + (bucketEnd - bucketStart) / 2;
                float calibrationDelta = recording.PowerOffsetAt(bucketCenter) - recording.RecordedPowerOffsetAt(bucketCenter);
                double bucketPower = Math.Pow(10,
                    (segment.RssiBucket(bucket) + CacheLevelMinimum + calibrationDelta) / 10d);
                int firstColumn = (int)((bucketVisibleStart - first) * columns / visibleSamples);
                int lastColumn = (int)((bucketVisibleEnd - 1 - first) * columns / visibleSamples);
                for (int column = firstColumn; column <= Math.Min(columns - 1, lastColumn); column++)
                {
                    long columnStart = first + visibleSamples * column / columns;
                    long columnEnd = first + visibleSamples * (column + 1) / columns;
                    long count = Math.Min(bucketVisibleEnd, columnEnd) - Math.Max(bucketVisibleStart, columnStart);
                    if (count <= 0) continue;
                    rssiPower[column] += bucketPower * count;
                    rssiCounts[column] += count;
                }
            }
        }
        for (int column = 0; column < columns; column++)
        {
            cancellation.ThrowIfCancellationRequested();
            levels[column] = (float)(10 * Math.Log10(Math.Max(rssiPower[column] / Math.Max(1, rssiCounts[column]), 1e-30)));
            if (column % 32 == 0) progress?.Report(column * 100 / columns);
        }
        progress?.Report(100);
        return new(first / (double)recording.SampleRate, last / (double)recording.SampleRate, levels, spectra);
    }

    private static CachedSpectrumSegment LoadOrCreateSegmentCache(Recording recording, int segmentIndex,
        int fftSampleCount, CancellationToken cancellation)
    {
        RecordingSegment segment = recording.Segments[segmentIndex];
        int bucketSamples = recording.SampleRate * BucketMilliseconds / 1000;
        int bucketCount = checked((int)((segment.Count + bucketSamples - 1) / bucketSamples));
        CacheSourceDependency[] lookahead = GetLookaheadDependencies(recording, segmentIndex, fftSampleCount);
        FileInfo source = new(segment.Path);
        CacheMetadataDependency metadata = GetMetadataDependency(segment);
        string cachePath = GetCachePath(segment.Path);
        if (TryReadCache(cachePath, source, metadata, recording.SampleRate, segment, bucketSamples, bucketCount,
            fftSampleCount, lookahead, out var cached)) return cached;

        SpectrumFrameSpec[] frames = GetFrameSpecs(segment.Count, recording.SampleRate, fftSampleCount);
        var peaks = new float[checked(bucketCount * Bins)];
        Array.Fill(peaks, float.NegativeInfinity);
        var populated = new bool[bucketCount];
        var analyzer = new SpectrumAnalyzer(fftSampleCount);
        var samples = new Complex32[fftSampleCount];
        var spectrum = new float[Bins];
        using (var reader = new RecordingReader(recording))
        {
            for (int i = 0; i < frames.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var frame = frames[i];
                reader.Seek(segment.Start + frame.Start);
                int read = reader.Read(samples.AsSpan(0, frame.Length));
                if (read == 0) throw new EndOfStreamException("録音データが途中で終了しました。");
                analyzer.Analyze(samples.AsSpan(0, read), spectrum);
                int bucket = Math.Min(bucketCount - 1, (int)(frame.Start / bucketSamples));
                populated[bucket] = true;
                for (int bin = 0; bin < Bins; bin++)
                {
                    int index = bucket * Bins + bin;
                    peaks[index] = Math.Max(peaks[index], spectrum[bin]);
                }
            }
        }

        int firstPopulated = Array.FindIndex(populated, isPopulated => isPopulated);
        if (firstPopulated >= 0)
        {
            for (int bucket = 0; bucket < firstPopulated; bucket++)
                Array.Copy(peaks, firstPopulated * Bins, peaks, bucket * Bins, Bins);
            int previous = firstPopulated;
            for (int bucket = firstPopulated + 1; bucket < bucketCount; bucket++)
            {
                if (populated[bucket]) previous = bucket;
                else Array.Copy(peaks, previous * Bins, peaks, bucket * Bins, Bins);
            }
        }

        var levels = new byte[peaks.Length];
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            long bucketStart = segment.Start + (long)bucket * bucketSamples;
            long bucketEnd = Math.Min(segment.Start + segment.Count, bucketStart + bucketSamples);
            long bucketCenter = bucketStart + (bucketEnd - bucketStart) / 2;
            float correction = recording.RecordedPowerOffsetAt(bucketCenter);
            for (int bin = 0; bin < Bins; bin++)
            {
                int index = bucket * Bins + bin;
                float db = float.IsFinite(peaks[index]) ? peaks[index] : -120f;
                levels[index] = EncodeLevel(db + correction);
            }
        }

        byte[] rssiLevels = CalculateRssiLevels(recording, segment, bucketSamples, bucketCount, cancellation);
        var result = new CachedSpectrumSegment(segment.Start, segment.Count, bucketSamples, levels, rssiLevels);
        WriteCache(cachePath, source, metadata, recording.SampleRate, segment, bucketSamples, bucketCount,
            fftSampleCount, lookahead, levels, rssiLevels);
        return result;
    }

    private static byte[] CalculateRssiLevels(Recording recording, RecordingSegment segment, int bucketSamples,
        int bucketCount, CancellationToken cancellation)
    {
        var levels = new byte[bucketCount];
        var samples = new Complex32[SpectrumAnalyzer.Size];
        using var reader = new RecordingReader(recording);
        reader.Seek(segment.Start);
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            long bucketEnd = Math.Min(segment.Start + segment.Count,
                segment.Start + (long)(bucket + 1) * bucketSamples);
            double power = 0;
            long count = 0;
            while (reader.Position < bucketEnd)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = reader.Read(samples.AsSpan(0, (int)Math.Min(samples.Length, bucketEnd - reader.Position)));
                if (read == 0) throw new InvalidOperationException("録音データを読み取れません。");
                foreach (var sample in samples.AsSpan(0, read))
                    power += (double)sample.I * sample.I + (double)sample.Q * sample.Q;
                count += read;
            }
            long bucketStart = segment.Start + (long)bucket * bucketSamples;
            long bucketCenter = bucketStart + (bucketEnd - bucketStart) / 2;
            float dbm = (float)(10 * Math.Log10(Math.Max(power / Math.Max(1, count), 1e-30)) +
                recording.RecordedPowerOffsetAt(bucketCenter));
            // Apply runtime manual calibration as a delta during display, without rereading IQ.
            levels[bucket] = EncodeLevel(dbm);
        }
        return levels;
    }

    private static CacheSourceDependency[] GetLookaheadDependencies(Recording recording, int segmentIndex, int fftSampleCount)
    {
        long samplesNeeded = fftSampleCount - 1L;
        var dependencies = new List<CacheSourceDependency>();
        for (int i = segmentIndex + 1; i < recording.Segments.Length && samplesNeeded > 0; i++)
        {
            RecordingSegment segment = recording.Segments[i];
            var source = new FileInfo(segment.Path);
            dependencies.Add(new(Path.GetFullPath(segment.Path), source.Length, source.LastWriteTimeUtc.Ticks,
                segment.Count, segment.Metadata?.QInverted == true));
            samplesNeeded -= Math.Min(samplesNeeded, segment.Count);
        }
        return dependencies.ToArray();
    }

    private static CacheMetadataDependency GetMetadataDependency(RecordingSegment segment)
    {
        var metadata = new FileInfo(Path.ChangeExtension(segment.Path, ".json"));
        return metadata.Exists ? new(metadata.Length, metadata.LastWriteTimeUtc.Ticks) : default;
    }

    private static SpectrumFrameSpec[] GetFrameSpecs(long sampleCount, int sampleRate, int fftSampleCount)
    {
        var result = new List<SpectrumFrameSpec>();
        long start = 0;
        int hopDenominator = 1000 / HopMilliseconds;
        int wholeHop = sampleRate / hopDenominator;
        int remainderHop = sampleRate % hopDenominator;
        int remainder = 0;
        while (start < sampleCount)
        {
            result.Add(new(start, fftSampleCount));
            start += wholeHop;
            remainder += remainderHop;
            if (remainder >= hopDenominator)
            {
                start += remainder / hopDenominator;
                remainder %= hopDenominator;
            }
        }
        return result.ToArray();
    }

    private static bool TryReadCache(string path, FileInfo source, CacheMetadataDependency metadata,
        int sampleRate, RecordingSegment segment,
        int bucketSamples, int bucketCount, int fftSampleCount, CacheSourceDependency[] expectedLookahead,
        out CachedSpectrumSegment cache)
    {
        cache = null!;
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != CacheVersion || reader.ReadInt64() != source.Length ||
                reader.ReadInt64() != source.LastWriteTimeUtc.Ticks || reader.ReadInt64() != metadata.Length ||
                reader.ReadInt64() != metadata.LastWriteTicks || reader.ReadInt32() != sampleRate ||
                reader.ReadInt64() != segment.Count || reader.ReadBoolean() != (segment.Metadata?.QInverted == true) ||
                reader.ReadInt32() != bucketSamples || reader.ReadInt32() != bucketCount ||
                reader.ReadInt32() != fftSampleCount || reader.ReadInt32() != expectedLookahead.Length)
                return false;
            foreach (var dependency in expectedLookahead)
                if (reader.ReadString() != dependency.Path || reader.ReadInt64() != dependency.Length ||
                    reader.ReadInt64() != dependency.LastWriteTicks || reader.ReadInt64() != dependency.SampleCount ||
                    reader.ReadBoolean() != dependency.QInverted)
                    return false;
            int dataLength = checked(bucketCount * Bins);
            byte[] levels = reader.ReadBytes(dataLength);
            byte[] rssiLevels = reader.ReadBytes(bucketCount);
            if (levels.Length != dataLength || rssiLevels.Length != bucketCount) return false;
            if (stream.Position != stream.Length) return false;
            cache = new(segment.Start, segment.Count, bucketSamples, levels, rssiLevels);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void WriteCache(string path, FileInfo source, CacheMetadataDependency metadata,
        int sampleRate, RecordingSegment segment,
        int bucketSamples, int bucketCount, int fftSampleCount, CacheSourceDependency[] lookahead, byte[] levels,
        byte[] rssiLevels)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(CacheVersion);
                writer.Write(source.Length);
                writer.Write(source.LastWriteTimeUtc.Ticks);
                writer.Write(metadata.Length);
                writer.Write(metadata.LastWriteTicks);
                writer.Write(sampleRate);
                writer.Write(segment.Count);
                writer.Write(segment.Metadata?.QInverted == true);
                writer.Write(bucketSamples);
                writer.Write(bucketCount);
                writer.Write(fftSampleCount);
                writer.Write(lookahead.Length);
                foreach (var dependency in lookahead)
                {
                    writer.Write(dependency.Path);
                    writer.Write(dependency.Length);
                    writer.Write(dependency.LastWriteTicks);
                    writer.Write(dependency.SampleCount);
                    writer.Write(dependency.QInverted);
                }
                writer.Write(levels);
                writer.Write(rssiLevels);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string GetCachePath(string wavPath) => Path.GetFullPath(wavPath) + ".fftcache";

    private static byte EncodeLevel(float db)
        => (byte)Math.Clamp((int)MathF.Round(db) - CacheLevelMinimum, 0, byte.MaxValue);

    public static void WriteColor(byte[] pixels, int offset, float db)
        => WriteColor(pixels, offset, db, -120, 0);

    public static void WriteColor(byte[] pixels, int offset, float db, float noiseFloorDb, int biasDb)
    {
        // Match SRdeck's waterfall: 0.25 dB LUT steps from its noise floor, plus wheel bias.
        int index = float.IsFinite(db) && float.IsFinite(noiseFloorDb)
            ? Math.Clamp((int)((db - noiseFloorDb) * 4 + biasDb), 0, 255) : 0;
        uint color = ColorLUT.GetLutBgr32(0)[index];
        pixels[offset] = (byte)(color & 255);
        pixels[offset + 1] = (byte)((color >> 8) & 255);
        pixels[offset + 2] = (byte)((color >> 16) & 255);
        pixels[offset + 3] = 255;
    }
}
