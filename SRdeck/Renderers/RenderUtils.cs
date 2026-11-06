using System.Runtime.CompilerServices;

namespace SRdeck.Renderers;

/// <summary>
/// 描画エンジン（Spectrum, Waterfall等）で共通使用される
/// 数値計算および座標スケーリング処理をまとめたユーティリティクラス
/// インライン展開を強制することで、描画ループ内でのオーバーヘッドを最小限に抑えます
/// </summary>
public static class RenderUtils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FreqToX(float hzOffset, double width, float displayBw)
    {
        return (float)(((hzOffset + (displayBw / 2.0f)) / displayBw) * width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SecToY(double sec, double height, double totalHistorySeconds = WaterfallTimeModel.TotalHistorySeconds)
    {
        return WaterfallTimeModel.SecondsToY(sec, height, totalHistorySeconds);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int XToFreqOffset(float x, double width, float displayBw)
    {
        return (int)((displayBw * x) / width) - (int)(displayBw / 2.0f);
    }

    public static (double MainHz, double SubHz) GetFrequencyGridSteps(double spanHz)
    {
        const double targetMainLines = 16.0;
        double raw = Math.Max(1.0, spanHz / targetMainLines);
        double pow10 = Math.Pow(10.0, Math.Floor(Math.Log10(raw)));
        double mantissa = raw / pow10;
        double nice = mantissa <= 1.0 ? 1.0 : mantissa <= 2.0 ? 2.0 : mantissa <= 5.0 ? 5.0 : 10.0;
        double mainHz = nice * pow10;
        return (mainHz, mainHz / 5.0);
    }

    /// <summary>
    /// 表示領域高さ (height) 上の Yピクセル座標から、元の履歴秒数 (sec) を復元します
    /// ※ウォーターフォールのクリック位置からの時間読み取り用
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int YToSec(float y, double height, double totalHistorySeconds = WaterfallTimeModel.TotalHistorySeconds)
    {
        return WaterfallTimeModel.YToSeconds(y, height, totalHistorySeconds);
    }
}
