namespace SRdeckPlugin.Contracts;

public sealed record FrequencyOverlayItem(
    string Id,
    long CenterFrequencyHz,
    int BandwidthHz,
    string Label,
    bool IsSelected,
    string Fill,
    string Stroke,
    string LabelColor,
    int Lane,
    bool IsEnabled = true,
    double Emphasis = 1.0,
    string? ToolTip = null);

public interface IFrequencyOverlayProvider
{
    IReadOnlyList<FrequencyOverlayItem> FrequencyOverlays { get; }
    event EventHandler? FrequencyOverlaysChanged;
}

/// <summary>
/// A time/frequency point drawn over the main waterfall. Times use the source
/// stream clock so annotations remain aligned during live reception and playback.
/// </summary>
public sealed record WaterfallAnnotationItem(
    string Id,
    DateTimeOffset Time,
    long FrequencyHz,
    string Color,
    string? Label = null,
    string? ToolTip = null);

public interface IWaterfallAnnotationProvider
{
    IReadOnlyList<WaterfallAnnotationItem> WaterfallAnnotations { get; }
    DateTimeOffset? WaterfallReferenceTime { get; }
    event EventHandler? WaterfallAnnotationsChanged;
}

/// <summary>
/// Controls how source FFT updates are mapped to rows in the main waterfall.
/// The host may fall back to <see cref="ThreeMinutes"/> for unknown values.
/// </summary>
public enum WaterfallTimeMode
{
    DoubleSpeed = 0,
    Uncompressed = 1,
    ThreeMinutes = 2,
    FifteenMinutes = 3,
    OneHour = 4
}

/// <summary>
/// A plugin's preferred main-waterfall presentation. The host treats these as
/// requests and clamps the bandwidth to the range supported by the active input.
/// A null bandwidth keeps the normal, unzoomed main display.
/// </summary>
public sealed record WaterfallDisplayRequest(
    WaterfallTimeMode TimeMode = WaterfallTimeMode.ThreeMinutes,
    int? PreferredDisplayBandwidthHz = null);

/// <summary>
/// Optional capability for plugins that benefit from a particular waterfall
/// time resolution or frequency span.
/// </summary>
public interface IWaterfallDisplayProvider
{
    WaterfallDisplayRequest WaterfallDisplayRequest { get; }
}

public interface IWaterfallDisplayRequestChangedProvider
{
    event EventHandler? WaterfallDisplayRequestChanged;
}

public enum PluginResultSeverity
{
    Trace,
    Information,
    Notice,
    Warning,
    Critical
}

public enum OverallStatusKind
{
    Idle,
    Running,
    Success,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Protocol-neutral result metadata for host notifications and integrations.
/// Detailed protocol data remains plugin-owned and is carried only as optional,
/// versioned JSON.
/// </summary>
public sealed record PluginResultSummary(
    string ResultId,
    string PluginId,
    DateTimeOffset ReceivedAt,
    Guid StreamId,
    string Kind,
    PluginResultSeverity Severity,
    string Title,
    string Summary,
    long? FrequencyHz = null,
    double? SignalQuality = null,
    int? DetailsSchemaVersion = null,
    string? DetailsJson = null);

public sealed class PluginResultPublishedEventArgs(PluginResultSummary result) : EventArgs
{
    public PluginResultSummary Result { get; } = result;
}

public interface IPluginResultProvider
{
    event EventHandler<PluginResultPublishedEventArgs>? ResultPublished;
}

public sealed record PluginExportFormat(
    string Id,
    string DisplayName,
    string FileExtension,
    string MediaType);

public sealed record PluginExportRequest(
    string FormatId,
    string DestinationPath,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record PluginExportResult(
    bool Succeeded,
    int ExportedItemCount,
    string Message);

public interface IPluginExportProvider
{
    IReadOnlyList<PluginExportFormat> ExportFormats { get; }
    ValueTask<PluginExportResult> ExportAsync(
        PluginExportRequest request,
        CancellationToken cancellationToken = default);
}
