namespace SRdeckPlugin.Contracts;

/// <summary>
/// Plugin settings serialized by the host. <see cref="SecretJsonPaths"/> is
/// classification metadata only; it does not encrypt <see cref="Json"/>.
/// </summary>
public sealed record PluginSettingsDocument(
    int SchemaVersion,
    string Json,
    IReadOnlyList<string>? SecretJsonPaths = null);

public interface IPluginSettingsStore
{
    string DataDirectory { get; }
    ValueTask<PluginSettingsDocument?> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(PluginSettingsDocument document, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(CancellationToken cancellationToken = default);
}

public readonly record struct TuningTarget(long FrequencyHz, int BandwidthHz);

public enum PluginGainPreference
{
    Unspecified,
    Automatic,
    Manual
}

/// <param name="MinimumSampleRateHz">
/// Minimum source rate required by the receiving mode, excluding the span between
/// selected channels. Only this minimum may cause the host to raise RATE automatically;
/// targets that do not fit the resulting passband are rejected without changing the input.
/// </param>
public sealed record PluginTuningRequest(
    string ProfileId,
    string DisplayName,
    IReadOnlyList<TuningTarget> Targets,
    long? PreferredCenterFrequencyHz,
    int MinimumSampleRateHz,
    int? FrequencyStepHz,
    bool RequiresContinuousReception,
    bool AllowsScanning,
    PluginGainPreference GainPreference,
    bool PreservePreferredCenterFrequency = false);

public enum PluginTuningOutcome
{
    Accepted,
    Adjusted,
    Rejected,
    Deferred
}

public sealed record PluginTuningResult(
    PluginTuningOutcome Outcome,
    string Message,
    long CenterFrequencyHz,
    int SampleRateHz,
    long PassbandLowerFrequencyHz,
    long PassbandUpperFrequencyHz,
    long TargetFrequencyHz = 0);

public interface IPluginTuningService
{
    PluginTuningResult Current { get; }
    event EventHandler<PluginTuningResult>? AppliedConfigurationChanged;
    ValueTask<PluginTuningResult> RequestAsync(
        PluginTuningRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for plugins whose primary receive frequency can be
/// selected from a host-owned station or band-plan catalog.
/// </summary>
public interface IPluginFrequencySelection
{
    long SelectedFrequencyHz { get; }
    /// <summary>Offset from the selected frequency to the center of its displayed receive band.</summary>
    int ReceiveBandCenterOffsetHz => 0;
    ValueTask<PluginTuningResult> SelectFrequencyAsync(
        long frequencyHz,
        CancellationToken cancellationToken = default);
}

public enum PcmSampleFormat
{
    Signed16LittleEndian
}

public sealed record PcmAudioFrame(
    string PluginId,
    Guid StreamId,
    long Sequence,
    int SampleRateHz,
    int Channels,
    PcmSampleFormat Format,
    ReadOnlyMemory<byte> Data,
    bool IsDiscontinuous,
    float? NormalizationGain = null);

public interface IPluginAudioSink
{
    /// <summary>
    /// Submits one audio frame. The sink copies <see cref="PcmAudioFrame.Data"/>
    /// before this method returns, so the producer may immediately reuse its buffer.
    /// </summary>
    bool TrySubmit(PcmAudioFrame frame);
    void Reset();
}

public interface IPluginNotificationService
{
    void PlayReceptionAlarm(TimeSpan delay = default);

    void PlayShortReceptionAlarm(TimeSpan delay = default)
    {
        PlayReceptionAlarm(delay);
    }
}
