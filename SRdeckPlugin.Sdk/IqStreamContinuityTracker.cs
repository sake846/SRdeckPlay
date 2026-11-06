using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sdk;

[Flags]
public enum IqStreamChange
{
    None = 0,
    FirstBlock = 1 << 0,
    StreamChanged = 1 << 1,
    GenerationChanged = 1 << 2,
    SequenceGap = 1 << 3,
    SamplePositionGap = 1 << 4,
    SampleRateChanged = 1 << 5,
    CenterFrequencyChanged = 1 << 6,
    ExplicitDiscontinuity = 1 << 7
}

public readonly record struct IqStreamTransition(
    IqStreamChange Changes,
    IqBlockMetadata Current,
    IqBlockMetadata? Previous)
{
    public bool RequiresReset => Changes != IqStreamChange.None;
}

/// <summary>
/// Tracks continuity between IQ blocks. Instances are intentionally not thread-safe;
/// observe blocks from the plugin's ordered processing worker.
/// </summary>
public sealed class IqStreamContinuityTracker
{
    private IqBlockMetadata previous;
    private bool hasPrevious;

    public IqStreamTransition Observe(in IqBlockMetadata current)
    {
        IqStreamChange changes = IqStreamChange.None;
        IqBlockMetadata? previousValue = hasPrevious ? previous : null;

        if (!hasPrevious)
        {
            changes = IqStreamChange.FirstBlock;
        }
        else
        {
            if (previous.StreamId != current.StreamId) changes |= IqStreamChange.StreamChanged;
            if (previous.Generation != current.Generation) changes |= IqStreamChange.GenerationChanged;
            if (previous.Sequence == long.MaxValue || current.Sequence != previous.Sequence + 1)
                changes |= IqStreamChange.SequenceGap;

            long expectedSampleStart;
            try { expectedSampleStart = checked(previous.AbsoluteSampleStart + previous.SampleCount); }
            catch (OverflowException) { expectedSampleStart = long.MinValue; }
            if (current.AbsoluteSampleStart != expectedSampleStart)
                changes |= IqStreamChange.SamplePositionGap;

            if (previous.SampleRateHz != current.SampleRateHz) changes |= IqStreamChange.SampleRateChanged;
            if (previous.CenterFrequencyHz != current.CenterFrequencyHz)
                changes |= IqStreamChange.CenterFrequencyChanged;
        }

        if (current.Discontinuity != IqDiscontinuity.None)
            changes |= IqStreamChange.ExplicitDiscontinuity;

        previous = current;
        hasPrevious = true;
        return new IqStreamTransition(changes, current, previousValue);
    }

    public void Reset()
    {
        previous = default;
        hasPrevious = false;
    }
}
