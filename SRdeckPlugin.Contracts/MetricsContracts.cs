namespace SRdeckPlugin.Contracts;

public enum PluginProcessingStage
{
    Input,
    Channelization,
    Detection,
    Synchronization,
    Demodulation,
    Validation,
    ProtocolDecode,
    Output
}

public enum PluginMetricKind
{
    Counter,
    Gauge,
    Duration
}

public sealed record PluginMetricValue(
    PluginProcessingStage Stage,
    string Name,
    PluginMetricKind Kind,
    double Value,
    string Unit,
    long UpdateCount);

public sealed record PluginMetricsSnapshot(
    string PluginId,
    DateTimeOffset MeasuredAt,
    IReadOnlyList<PluginMetricValue> Values);

public enum PluginComputeDevice
{
    Unknown,
    Cpu,
    Gpu,
    Mixed
}

/// <summary>
/// Plugin-owned description of the processing performed after the host has
/// delivered either raw or channelized IQ. Current built-in plugins expose one
/// combined stage so the host can time the whole callback without guessing how
/// time is divided between internal algorithms.
/// </summary>
public sealed record PluginProcessingStageDefinition(
    string Operation,
    PluginComputeDevice Device,
    string Backend,
    string? Detail = null);

/// <summary>
/// Optional capability used by the common diagnostics UI to identify an
/// plugin's compute path.
/// </summary>
public interface IPluginProcessingDiagnosticsProvider
{
    PluginProcessingStageDefinition ProcessingStage { get; }
}

/// <summary>A measured processing stage shown in the common diagnostics UI.</summary>
public sealed record PluginProcessingStageSnapshot(
    string Operation,
    PluginComputeDevice Device,
    string Backend,
    double CurrentProcessingTimeMs,
    double AverageProcessingTimeMs,
    long MeasurementCount,
    string? Detail = null);

/// <summary>
/// Host-owned, protocol-neutral diagnostics for the plugin IQ delivery path.
/// Processing load is derived by the presentation layer from processing time
/// divided by the input block duration and may exceed 100 percent.
/// </summary>
public readonly record struct PluginRuntimeDiagnosticsSnapshot(
    long SubmittedBlocks,
    long ProcessedBlocks,
    long DroppedBlocks,
    long DroppedSamples,
    int QueueDepth,
    int MaximumQueueDepth,
    int OutstandingLeases,
    double CurrentProcessingTimeMs,
    double CurrentBlockDurationMs,
    double AverageProcessingTimeMs,
    double MaximumProcessingTimeMs,
    long LastProcessedSequence,
    DateTimeOffset? LastSuccessfulProcessingUtc,
    string? LastError,
    IReadOnlyList<PluginProcessingStageSnapshot>? ProcessingStages);

public interface IPluginRuntimeDiagnostics
{
    PluginRuntimeDiagnosticsSnapshot GetSnapshot();
}

public sealed class NullPluginRuntimeDiagnostics : IPluginRuntimeDiagnostics
{
    public static NullPluginRuntimeDiagnostics Instance { get; } = new();
    private NullPluginRuntimeDiagnostics() { }
    public PluginRuntimeDiagnosticsSnapshot GetSnapshot() => default;
}

public interface IPluginMetrics
{
    void AddCounter(PluginProcessingStage stage, string name, long delta = 1, string unit = "count");
    void SetGauge(PluginProcessingStage stage, string name, double value, string unit);
    void RecordDuration(PluginProcessingStage stage, string name, TimeSpan elapsed);
}

public sealed class NullPluginMetrics : IPluginMetrics
{
    public static NullPluginMetrics Instance { get; } = new();
    private NullPluginMetrics() { }
    public void AddCounter(PluginProcessingStage stage, string name, long delta = 1, string unit = "count") { }
    public void SetGauge(PluginProcessingStage stage, string name, double value, string unit) { }
    public void RecordDuration(PluginProcessingStage stage, string name, TimeSpan elapsed) { }
}
