using System.Runtime.CompilerServices;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;

namespace SRdeck.Renderers;

/// <summary>
/// ウォーターフォールの配色（カラーパレット）を管理する静的なユーティリティクラス
/// 描画負荷を下げるため、起動時および色変更時にのみLUT（Look Up Table）を再計算し、
/// 以降の配色アクセスのみでO(1)で色を引き当てます
/// </summary>
internal static class ColorLUT
{
    private static uint[] _lutBgr32 = new uint[256];
    private static int _currentColorMode = -1;

    /// <summary>
    /// 現在のカラーモードに適合した256要素のBgr32カラールックアップテーブルを取得します
    /// 指定された colorMode が前回と異なる場合は、自動的にLUTを再構築します
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint[] GetLutBgr32(int colorMode)
    {
        if (colorMode != _currentColorMode)
        {
            RebuildLut(colorMode);
        }
        return _lutBgr32;
    }

    private static void RebuildLut(int colorMode)
    {
        for (int i = 0; i < 256; i++)
        {
            // 0-199 の範囲で色が変化しきるようにマッピング
            // i=0   -> level=0   (暗所/開始)
            // i=199 -> level=255 (明所/終了)
            // 200以上は 255 でクランチ
            int level = (int)Math.Clamp(i * (255.0 / 199.0), 0, 255);
            
            int r = 0, g = 0, blue = 0;
            GetWfColorNormalized(level, colorMode, ref r, ref g, ref blue);
            
            _lutBgr32[i] = (uint)((255 << 24) | (r << 16) | (g << 8) | blue);
        }
        _currentColorMode = colorMode;
    }

    /// <summary>
    /// 正規化された輝度(0-255) に基づく配色ロジック
    /// </summary>
    private static void GetWfColorNormalized(int value, int colorMode, ref int pRed, ref int pGreen, ref int pBlue)
    {
        // 0(黒に近い青) -> 64(鮮やかな青) -> 128(緑) -> 192(黄) -> 255(赤)
        if (value < 64) // 黒 -> 青
        {
            pRed = 0; pGreen = 0; pBlue = value * 4;
        }
        else if (value < 128) // 青 -> 緑
        {
            pRed = 0; pGreen = (value - 64) * 4; pBlue = 255 - (value - 64) * 4;
        }
        else if (value < 192) // 緑 -> 黄
        {
            pRed = (value - 128) * 4; pGreen = 255; pBlue = 0;
        }
        else // 黄 -> 赤
        {
            pRed = 255; pGreen = 255 - (value - 192) * 4; pBlue = 0;
        }
        
        pRed = Math.Clamp(pRed, 0, 255);
        pGreen = Math.Clamp(pGreen, 0, 255);
        pBlue = Math.Clamp(pBlue, 0, 255);
    }

    /// <summary>
    /// スペクトラム描画モードに応じた塗りつぶし色とストローク色を取得します
    /// </summary>
    internal static void GetSpectrumColors(int colorMode, out Color fill, out Color stroke)
    {
        fill = new Color(0f, 80f / 255f, 210f / 255f, 120f / 255f);
        stroke = new Color(40f / 255f, 150f / 255f, 1f, 1f);
    }
}
