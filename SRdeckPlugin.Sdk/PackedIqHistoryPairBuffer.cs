using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sdk;

public sealed record PackedIqHistoryPairSnapshot(
    PackedIqHistorySnapshot First,
    PackedIqHistorySnapshot Second);

/// <summary>Atomically stores and snapshots two related IQ streams with independent rates.</summary>
public sealed class PackedIqHistoryPairBuffer
{
    private readonly object gate = new();
    private readonly PackedIqRing first;
    private readonly PackedIqRing second;

    public PackedIqHistoryPairBuffer(int durationSeconds)
    {
        first = new(durationSeconds);
        second = new(durationSeconds);
    }

    public void Reset()
    {
        lock (gate)
        {
            first.Reset();
            second.Reset();
        }
    }

    public void Write(ReadOnlySpan<Complex32> firstSamples, int firstSampleRateHz,
        ReadOnlySpan<Complex32> secondSamples, int secondSampleRateHz)
    {
        lock (gate)
        {
            first.Write(firstSamples, firstSampleRateHz);
            second.Write(secondSamples, secondSampleRateHz);
        }
    }

    public PackedIqHistoryPairSnapshot? TakeSnapshot()
    {
        lock (gate)
        {
            PackedIqHistorySnapshot? firstSnapshot = first.TakeSnapshot();
            PackedIqHistorySnapshot? secondSnapshot = second.TakeSnapshot();
            return firstSnapshot is null || secondSnapshot is null
                ? null
                : new(firstSnapshot, secondSnapshot);
        }
    }
}
