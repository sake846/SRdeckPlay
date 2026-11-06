namespace SRdeckPlugin.Contracts;

public enum PluginSpectrumRegion { InputBand, DisplayBand, FrequencyRange }

/// <summary>Aggregation of the shared display-spectrum bins, not integrated RF band power.</summary>
public enum PluginSpectrumAggregation { Peak, MeanPower }

/// <summary>Optional absolute frequency edges are required only for FrequencyRange.</summary>
public sealed record PluginSpectrumRequest(int BinCount,
    PluginSpectrumRegion Region = PluginSpectrumRegion.InputBand,
    PluginSpectrumAggregation Aggregation = PluginSpectrumAggregation.Peak,
    double? LowerFrequencyHz = null, double? UpperFrequencyHz = null)
{
    public const int MaximumBinCount = 16_384;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BinCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BinCount, MaximumBinCount);
        if (!Enum.IsDefined(Region) || !Enum.IsDefined(Aggregation))
            throw new ArgumentException("Unknown spectrum region or aggregation.");
        if (Region == PluginSpectrumRegion.FrequencyRange)
        {
            if (LowerFrequencyHz is not { } lower || UpperFrequencyHz is not { } upper ||
                !double.IsFinite(lower) || !double.IsFinite(upper) || lower >= upper || !double.IsFinite(upper - lower))
                throw new ArgumentException("A finite increasing frequency range is required.");
        }
        else if (LowerFrequencyHz is not null || UpperFrequencyHz is not null)
            throw new ArgumentException("Explicit edges require FrequencyRange.");
    }
}

/// <summary>
/// Immutable, caller-retainable copy of the host display spectrum, in ascending frequency order.
/// Each output cell spans [LowerFrequencyHz + n * BinWidthHz, LowerFrequencyHz + (n+1) * BinWidthHz).
/// Uncaptured cells are NaN; partially captured cells aggregate only captured bins.
/// SourceBinCount describes the shared display-spectrum resolution before this request's resampling.
/// TimestampUtc is the FFT publication time; FrameId identifies that FFT independently of view changes.
/// </summary>
public sealed record PluginSpectrumSnapshot(long FrameId, DateTimeOffset TimestampUtc,
    int SampleRateHz, long InputCenterFrequencyHz, double LowerFrequencyHz, double UpperFrequencyHz,
    int SourceBinCount, PluginSpectrumAggregation Aggregation, ReadOnlyMemory<float> LevelsDbm)
{
    public double BinWidthHz => (UpperFrequencyHz - LowerFrequencyHz) / LevelsDbm.Length;
    /// <summary>dBm correction applied to the source display-spectrum levels.</summary>
    public float CalibrationOffsetDb { get; init; }
}

/// <summary>
/// Optional read-only shared FFT service for monitors, plots and frequency analysis.
/// Uses the host display's dBm calibration. Does not change tuning or FFT settings.
/// FrameId identifies the source FFT, not the query: view, aggregation and calibration changes can
/// return different values for the same FrameId. Repeated identical queries reuse an immutable snapshot.
/// Null means no valid FFT is available. Invalid requests throw ArgumentException.
/// </summary>
public interface IPluginSpectrumService
{
    PluginSpectrumSnapshot? GetSnapshot(PluginSpectrumRequest request);
}

public sealed class NullPluginSpectrumService : IPluginSpectrumService
{
    public static NullPluginSpectrumService Instance { get; } = new();
    private NullPluginSpectrumService() { }
    public PluginSpectrumSnapshot? GetSnapshot(PluginSpectrumRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return null;
    }
}
