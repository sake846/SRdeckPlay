using System.Runtime.CompilerServices;
using SRdeckPlugin.Contracts;

namespace SRdeck.Renderers;

/// <summary>
/// ウォーターフォールの時間軸に関する共通仕様です。
/// 描画、カーソル、ズーム枠で同じ換算式を使うため、時間軸の基準値をここに集約します。
/// </summary>
public static class WaterfallTimeModel
{
    public const double TotalHistorySeconds = 180.0;
    public const double FifteenMinutesHistorySeconds = 900.0;
    public const double OneHourHistorySeconds = 3600.0;
    public const double SourceRowDurationMs = 100.0;
    public const double DoubleSpeedRowDurationMs = 50.0;
    public const double TopLabelHeightPx = 18.0;
    public const double FifteenMinutesTickIntervalSeconds = 60.0;
    public const double OneHourTickIntervalSeconds = 300.0;
    private const double UncompressedFiveSecondTickLimit = 90.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetPlotHeight(double totalHeight)
        => totalHeight - TopLabelHeightPx;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetRowDurationMs(double plotHeight)
        => TotalHistorySeconds * 1000.0 / plotHeight;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetRowDurationMs(WaterfallTimeMode timeMode, double plotHeight)
        => timeMode switch
        {
            WaterfallTimeMode.DoubleSpeed => DoubleSpeedRowDurationMs,
            WaterfallTimeMode.Uncompressed => SourceRowDurationMs,
            WaterfallTimeMode.FifteenMinutes => FifteenMinutesHistorySeconds * 1000.0 / Math.Max(1.0, plotHeight),
            WaterfallTimeMode.OneHour => OneHourHistorySeconds * 1000.0 / Math.Max(1.0, plotHeight),
            _ => TotalHistorySeconds * 1000.0 / Math.Max(1.0, plotHeight)
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double GetTotalHistorySeconds(WaterfallTimeMode timeMode, double rasterHeight)
        => timeMode switch
        {
            WaterfallTimeMode.DoubleSpeed => Math.Max(1.0, rasterHeight) * DoubleSpeedRowDurationMs / 1000.0,
            WaterfallTimeMode.Uncompressed => Math.Max(1.0, rasterHeight) * SourceRowDurationMs / 1000.0,
            WaterfallTimeMode.FifteenMinutes => FifteenMinutesHistorySeconds,
            WaterfallTimeMode.OneHour => OneHourHistorySeconds,
            _ => TotalHistorySeconds
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetUncompressedTickIntervalSeconds(double totalHistorySeconds)
        => totalHistorySeconds <= 45.0 ? 2 : (totalHistorySeconds <= UncompressedFiveSecondTickLimit ? 5 : 10);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SecondsToY(double seconds, double plotHeight)
        => (float)((seconds / TotalHistorySeconds) * plotHeight);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SecondsToY(double seconds, double plotHeight, double totalHistorySeconds)
        => (float)((seconds / Math.Max(double.Epsilon, totalHistorySeconds)) * plotHeight);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int YToSeconds(double y, double plotHeight)
        => (int)(TotalHistorySeconds * y / plotHeight);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int YToSeconds(double y, double plotHeight, double totalHistorySeconds)
        => (int)(Math.Max(double.Epsilon, totalHistorySeconds) * y / Math.Max(1.0, plotHeight));
}
