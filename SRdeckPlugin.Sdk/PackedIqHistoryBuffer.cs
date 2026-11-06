using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sdk;

public sealed record PackedIqHistorySnapshot(
    int SampleRateHz,
    short[] RawInterleaved,
    double DurationSeconds);

public sealed class PackedIqHistoryBuffer
{
    private readonly object gate = new();
    private readonly PackedIqRing ring;

    public PackedIqHistoryBuffer(int durationSeconds) => ring = new(durationSeconds);

    public void Reset()
    {
        lock (gate) ring.Reset();
    }

    public void Write(ReadOnlySpan<Complex32> samples, int inputSampleRateHz)
    {
        lock (gate) ring.Write(samples, inputSampleRateHz);
    }

    public PackedIqHistorySnapshot? TakeSnapshot()
    {
        lock (gate) return ring.TakeSnapshot();
    }
}
