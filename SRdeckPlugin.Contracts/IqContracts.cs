namespace SRdeckPlugin.Contracts;

public readonly record struct Complex32(float I, float Q);

public enum IqInputSource
{
    Sdr,
    Playback
}

[Flags]
public enum IqDiscontinuity
{
    None = 0,
    StreamStarted = 1 << 0,
    SamplesDropped = 1 << 1,
    TuningChanged = 1 << 2,
    SampleRateChanged = 1 << 3
}

public readonly record struct IqBlockMetadata(
    Guid StreamId,
    long Generation,
    long Sequence,
    long AbsoluteSampleStart,
    long MonotonicTimestamp,
    DateTimeOffset UtcTimestamp,
    int SampleRateHz,
    long CenterFrequencyHz,
    int SampleCount,
    IqInputSource InputSource,
    IqDiscontinuity Discontinuity);

/// <summary>
/// A read-only, normalized IQ block. Samples are valid only for the duration
/// of IIqBlockConsumer.ConsumeAsync. Hosts must release the backing memory
/// after the call completes, including exceptional completion.
/// </summary>
public interface IIqBlockLease : IDisposable
{
    IqBlockMetadata Metadata { get; }
    ReadOnlyMemory<Complex32> Samples { get; }
}

public readonly record struct PluginIqPreferences(int RequestedQueueCapacity)
{
    public static PluginIqPreferences Default { get; } = new(4);
}

public interface IIqBlockConsumer
{
    PluginIqPreferences IqPreferences { get; }
    ValueTask ConsumeAsync(IIqBlockLease block, CancellationToken cancellationToken);
}

public enum PluginChannelAccelerationPreference
{
    Auto,
    Cpu,
    GpuPreferred,
    GpuRequired
}

/// <summary>
/// Describes a reusable host-provided baseband channel. <see cref="BandwidthHz"/>
/// is the occupied passband width to preserve, not a transition-band or filter
/// cutoff value. <see cref="StopbandBandwidthHz"/> is an optional, explicit
/// full-width boundary at which the host channelizer's rejection begins. It is
/// used to design the anti-alias/channel-isolation filter around rate conversion;
/// it does not describe a post-demodulation audio filter. Zero retains the
/// compatibility behavior of using the available output Nyquist width.
/// </summary>
public readonly record struct PluginChannelRequest(
    string Id,
    long CenterFrequencyHz,
    int BandwidthHz,
    int OutputSampleRateHz,
    int MaximumIntermediateSampleRateHz = 0,
    int MinimumIntermediateSampleRateHz = 0,
    int FirTaps = 33,
    int CicStages = 2,
    int RequestedQueueCapacity = 4,
    bool AllowRawIqFallback = true,
    int CoarseOutputMinimumSampleRateHz = 0,
    int CoarseOutputMaximumSampleRateHz = 0,
    int MaximumFineDecimationFactor = 1,
    PluginChannelAccelerationPreference AccelerationPreference =
        PluginChannelAccelerationPreference.Auto)
{
    public int StopbandBandwidthHz { get; init; }
}

/// <summary>The exact channelizer configuration selected by the host.</summary>
public readonly record struct AppliedChannelConfiguration(
    string RequestId,
    long ChannelCenterFrequencyHz,
    long InputCenterFrequencyHz,
    int InputSampleRateHz,
    int OutputSampleRateHz,
    int BandwidthHz,
    int CoarseDecimationFactor,
    int FineDecimationFactor,
    int InterpolationFactor,
    int ResamplerDecimationFactor,
    int FirTaps,
    int CicStages,
    double GroupDelayInputSamples,
    string ProcessingBackend);

/// <summary>
/// Metadata for a channelized block. <see cref="SourceSampleOrigin"/> is the
/// source-stream position corresponding to output position zero before group
/// delay compensation. A source position for any output sample can therefore
/// be reconstructed without depending on host implementation details.
/// </summary>
public readonly record struct ChannelIqBlockMetadata(
    IqBlockMetadata Source,
    long OutputSampleStart,
    long SourceSampleOrigin,
    int SampleCount,
    AppliedChannelConfiguration Configuration)
{
    public long MapOutputToSource(double outputSamplePosition) => checked(
        SourceSampleOrigin + (long)Math.Round(
            outputSamplePosition * Configuration.InputSampleRateHz /
            (double)Configuration.OutputSampleRateHz -
            Configuration.GroupDelayInputSamples));
}

public interface IChannelIqBlockLease : IDisposable
{
    ChannelIqBlockMetadata Metadata { get; }
    ReadOnlyMemory<Complex32> Samples { get; }
}

/// <summary>
/// Optional capability for plugins that consume a standard host-channelized
/// stream. An plugin may also implement <see cref="IIqBlockConsumer"/> as a
/// compatibility or specialized-processing fallback.
/// </summary>
public interface IPluginChannelBlockConsumer
{
    IReadOnlyList<PluginChannelRequest> ChannelRequests { get; }
    ValueTask ConsumeChannelsAsync(
        IReadOnlyList<IChannelIqBlockLease> blocks,
        CancellationToken cancellationToken);
}
