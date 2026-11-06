using System.IO;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Sdk;

namespace SRdeckPlugin.Analog;

internal sealed class AnalogNarrowbandIqRecording
{
    private const int DefaultSegmentDurationSeconds = 600;
    private const int WideFmSegmentDurationSeconds = 60;

    private readonly string directory;
    private readonly string profile;
    private readonly long frequencyHz;
    private readonly int bandwidthHz;
    private readonly int segmentDurationSeconds;
    private readonly string recordingId = Guid.NewGuid().ToString("N");
    private BoundedIqWavWriter? writer;
    private IqBlockMetadata? firstMetadata, previousMetadata;
    private int sampleRateHz, segmentNumber;
    private long segmentSampleCount, totalSampleCount, expectedInputSample, segmentStartSampleCount;
    private int sourceDiscontinuityCount;
    private readonly List<IqRecordingTimelinePoint> timeline = [];
    private long segmentMissingInputSamples;
    private int segmentDiscontinuityCount;
    private double totalSeconds;
    private DateTimeOffset segmentStartUtc;
    private string? segmentWavPath;
    private bool finished;

    public AnalogNarrowbandIqRecording(string directory, string profile, long frequencyHz, int bandwidthHz)
    {
        this.directory = directory;
        this.profile = profile;
        this.frequencyHz = frequencyHz;
        this.bandwidthHz = bandwidthHz;
        segmentDurationSeconds = profile == "fm" && bandwidthHz > 50_000
            ? WideFmSegmentDurationSeconds
            : DefaultSegmentDurationSeconds;
    }

    public string? LastPath { get; private set; }
    public string? StagingPath => segmentWavPath is null ? null : segmentWavPath + ".tmp";
    public int SampleRateHz => sampleRateHz;
    public int SegmentCount => segmentNumber;
    public double Seconds => totalSeconds;
    public bool HasSamples => totalSampleCount > 0;
    public int SourceDiscontinuityCount => sourceDiscontinuityCount;

    public string? Write(
        ReadOnlySpan<Complex32> samples,
        int currentSampleRateHz,
        IqBlockMetadata metadata,
        string currentProfile,
        long currentFrequencyHz,
        int currentBandwidthHz,
        float? calibrationOffsetDb = null)
    {
        if (finished) throw new InvalidOperationException("Recording already finished.");
        if (currentProfile != profile || currentFrequencyHz != frequencyHz || currentBandwidthHz != bandwidthHz)
            return "受信設定変更";
        if (samples.IsEmpty) return null;
        if (currentSampleRateHz <= 0 || metadata.SampleRateHz <= 0 || metadata.SampleCount <= 0)
            return "入力形式変更";
        if (calibrationOffsetDb is { } correction && !float.IsFinite(correction)) calibrationOffsetDb = null;
        bool discontinuity = false;
        long missing = 0;
        if (previousMetadata is { } previous)
        {
            discontinuity = metadata.StreamId != previous.StreamId ||
                metadata.Generation != previous.Generation ||
                metadata.SampleRateHz != previous.SampleRateHz ||
                metadata.AbsoluteSampleStart != expectedInputSample ||
                metadata.Discontinuity != IqDiscontinuity.None;
            if (discontinuity)
            {
                sourceDiscontinuityCount++;
                if (metadata.StreamId == previous.StreamId &&
                    metadata.Generation == previous.Generation &&
                    metadata.SampleRateHz == previous.SampleRateHz &&
                    metadata.AbsoluteSampleStart > expectedInputSample)
                {
                    missing = metadata.AbsoluteSampleStart - expectedInputSample;
                }
            }
        }
        if (sampleRateHz != 0 && currentSampleRateHz != sampleRateHz && writer is not null)
            FinishSegment("内部サンプルレート変更", isFinalSegment: false);
        sampleRateHz = currentSampleRateHz;

        int offset = 0;
        while (offset < samples.Length)
        {
            if (writer is null) StartSegment(metadata, offset);
            if (offset == 0 && discontinuity)
            {
                segmentDiscontinuityCount++;
                segmentMissingInputSamples += missing;
            }
            if (timeline.Count == 0 || (offset == 0 &&
                (discontinuity || timeline[^1].CalibrationOffsetDb != calibrationOffsetDb)))
            {
                // Gain changes retain the sample clock; only source discontinuities establish a new clock.
                DateTimeOffset utc = timeline.Count == 0 || discontinuity
                    ? metadata.UtcTimestamp.AddSeconds(offset / (double)sampleRateHz)
                    : timeline[^1].UtcTimestamp.AddSeconds((segmentSampleCount - timeline[^1].SampleOffset) / (double)sampleRateHz);
                timeline.Add(new(segmentSampleCount, utc, calibrationOffsetDb, offset == 0 && discontinuity,
                    offset == 0 ? missing : 0));
            }
            int count = (int)Math.Min(
                samples.Length - offset,
                segmentDurationSeconds * (long)sampleRateHz - segmentSampleCount);
            writer!.Write(samples.Slice(offset, count));
            segmentSampleCount += count;
            totalSampleCount += count;
            totalSeconds += count / (double)sampleRateHz;
            offset += count;
            if (writer!.IsComplete)
                FinishSegment(segmentDurationSeconds == WideFmSegmentDurationSeconds ? "1分区切り" : "10分区切り",
                    isFinalSegment: false);
        }

        previousMetadata = metadata;
        expectedInputSample = metadata.AbsoluteSampleStart + metadata.SampleCount;
        return null;
    }

    public string? Finish(string reason)
    {
        if (finished) return LastPath;
        finished = true;
        if (writer is not null) FinishSegment(reason, isFinalSegment: true);
        return LastPath;
    }

    private void StartSegment(IqBlockMetadata metadata, int sampleOffset)
    {
        Directory.CreateDirectory(directory);
        segmentNumber++;
        segmentWavPath = System.IO.Path.Combine(directory,
            $"analog-narrowband-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{frequencyHz}Hz-{recordingId}-{segmentNumber:D3}.wav");
        writer = new(segmentWavPath + ".tmp", sampleRateHz,
            TimeSpan.FromSeconds(segmentDurationSeconds));
        firstMetadata = metadata;
        segmentStartSampleCount = totalSampleCount;
        segmentStartUtc = metadata.UtcTimestamp.AddSeconds(sampleOffset / (double)sampleRateHz);
        segmentDiscontinuityCount = 0;
        segmentMissingInputSamples = 0;
        timeline.Clear();
    }

    private void FinishSegment(string reason, bool isFinalSegment)
    {
        if (writer is null || firstMetadata is not { } metadata || segmentWavPath is not { } wavPath) return;
        BoundedIqWavWriter currentWriter = writer;
        writer = null;
        currentWriter.Dispose();

        string jsonPath = System.IO.Path.ChangeExtension(wavPath, ".json");
        string gainPath = System.IO.Path.ChangeExtension(wavPath, ".gain");
        using (var file = new FileStream(jsonPath + ".tmp", FileMode.CreateNew))
            JsonSerializer.Serialize(file, new
            {
                SchemaVersion = 2,
                PluginId = "analog",
                RecordingId = recordingId,
                SegmentNumber = segmentNumber,
                IsFinalSegment = isFinalSegment,
                Profile = profile,
                CenterFrequencyHz = frequencyHz,
                BandwidthHz = bandwidthHz,
                SampleRateHz = sampleRateHz,
                CaptureSignal = "Post-rate-conversion, pre-channel-filter complex IQ; CIC compensated when decimated",
                SampleCount = segmentSampleCount,
                DurationSeconds = segmentSampleCount / (double)sampleRateHz,
                RecordingSampleStart = segmentStartSampleCount,
                StartUtc = segmentStartUtc.ToLocalTime(),
                EndUtc = timeline[^1].UtcTimestamp.AddSeconds((segmentSampleCount - timeline[^1].SampleOffset) / (double)sampleRateHz).ToLocalTime(),
                SampleFormat = "PCM16 stereo I/Q",
                QInverted = false,
                AmplitudeCalibrated = timeline.All(point => point.CalibrationOffsetDb is not null),
                CalibrationOffsetDb = timeline[0].CalibrationOffsetDb,
                Timeline = timeline.Select(point => point with { UtcTimestamp = point.UtcTimestamp.ToLocalTime() }).ToArray(),
                StopReason = reason,
                SourceDiscontinuityCount = segmentDiscontinuityCount,
                EstimatedMissingInputSamples = segmentMissingInputSamples,
                SourceMetadata = metadata with { UtcTimestamp = metadata.UtcTimestamp.ToLocalTime() }
            }, new JsonSerializerOptions { WriteIndented = true });
        using (var file = new BinaryWriter(new FileStream(gainPath + ".tmp", FileMode.CreateNew)))
        {
            file.Write(0L);
            file.Write(0f);
            file.Write(checked((int)frequencyHz));
        }

        File.Move(jsonPath + ".tmp", jsonPath);
        File.Move(gainPath + ".tmp", gainPath);
        File.Move(wavPath + ".tmp", wavPath);
        LastPath = wavPath;
        segmentWavPath = null;
        firstMetadata = null;
        segmentSampleCount = 0;
    }
}
