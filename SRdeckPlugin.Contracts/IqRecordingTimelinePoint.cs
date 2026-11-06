namespace SRdeckPlugin.Contracts;

/// <summary>A sample-aligned wall clock and dBFS-to-dBm calibration anchor in an IQ WAV segment.</summary>
public sealed record IqRecordingTimelinePoint(
    long SampleOffset,
    DateTimeOffset UtcTimestamp,
    float? CalibrationOffsetDb,
    bool IsDiscontinuity,
    long MissingInputSamples = 0);
