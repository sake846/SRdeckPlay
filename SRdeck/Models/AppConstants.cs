using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SRdeck.Models;

/// <summary>
/// アプリケーション全体で使用される定数群を定義します。以前のレンダラーやViewModelに直書き（マジックナンバー）されていたものを集約しました。
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// メインFFTのサイズです。
    /// </summary>
    public const int FFT_SIZE = 4096;

    /// <summary>
    /// 最低RSSI（受信信号強度）のdBm値です。
    /// スペクトラムやウォーターフォールの色表示の下限などに使用されます。
    /// </summary>
    public const float MIN_RSSI_DB = -150f;

    /// <summary>
    /// メインサンプリングレート (8.0MHz) です。
    /// </summary>
    public const float FULL_BW = 8000000f;

    /// <summary>
    /// スペクトラムおよびウォーターフォールの表示帯域 (7.0MHz) です。
    /// </summary>
    public const float DISPLAY_BW = 7000000f;

    /// <summary>
    /// アンテナ端の入力信号強度 (dBm) と内部FFT値の整合性をとるための物理補正定数 (マスターオフセット) です。
    /// 実機SG (信号発生器) による -60dBm 入力時に表示が一致するよう +85.0dB を定数として定義します。
    /// </summary>
    public const float RF_CAL_OFFSET = 85.0f;

    /// <summary>
    /// スペクトラム表示のデフォルトダイナミックレンジ (dB) です。
    /// </summary>
    public const float SPECTRUM_VIEW_RANGE_DB = 100.0f;

    /// <summary>
    /// スペクトラムグリッドの最上端のデフォルト dBm 値です。
    /// </summary>
    public const float DEFAULT_GRID_TOP_DB = -40.0f;
    
    /// <summary>
    /// FFT データの統計計算（ノイズフロア算出等）において、両端から除外する帯域の割合（%）です。
    /// </summary>
    public const int FFT_SCAN_MARGIN_PERCENT = 10;

    /// <summary>
    /// RSSI 履歴や周波数履歴を記録するための統計グリッドのサイズです。
    /// </summary>
    public const int STATISTICS_GRID_SIZE = 1800;

    /// <summary>
    /// ADC オーバーフローやクリッピングを防ぐための AGC 制御上限値です（約 -6.0 dBFS）。
    /// </summary>
    public const short AGC_UPPER_THRESHOLD = 16423;

    /// <summary>
    /// ADC のダイナミックレンジを確保するための AGC 制御下限値です（約 -18.0 dBFS）。
    /// </summary>
    public const short AGC_LOWER_THRESHOLD = 4125;

    /// <summary>
    /// AGC の初期プロファイルに使うリリース間隔（秒）です。
    /// </summary>
    public const double AGC_RELEASE_FAST_SECONDS = 0.1;
    public const double AGC_RELEASE_MEDIUM_SECONDS = 1.0;
    public const double AGC_RELEASE_SLOW_SECONDS = 10.0;

    /// <summary>
    /// RSSI および FFT パワーの平滑化に使用する指数移動平均 (EMA) の係数です。
    /// </summary>
    public const float RSSI_EMA_ALPHA = 0.1f;

    /// <summary>
    /// メインウィンドウのデフォルトタイトルです。
    /// </summary>
    public const string DEFAULT_WINDOW_TITLE = "SRdeck";

    /// <summary>
    /// IQリングバッファの固定保持時間です。
    /// </summary>
    public const int IQ_RETENTION_SECONDS = 10;
    public const int MAX_HISTORY_SEC = IQ_RETENTION_SECONDS - 1;

    /// <summary>
    /// 選択可能な周波数ステップのプリセットリスト (Hz) です。
    /// </summary>
    public static readonly int[] STEP_LEVELS = { 10, 100, 500, 1000, 5000, 6250, 8333, 9000, 10000, 12500, 15000, 20000, 25000, 30000, 50000, 100000 };


    /// <summary>
    /// SDR デバイスのデフォルトバイアス (PPM) です。
    /// </summary>
    public const float DEFAULT_SDR_BIAS_PPM = -0.04f;

    /// <summary>
    /// 最小利得低減量のデフォルト値 (dB) です。
    /// </summary>
    public const int DEFAULT_MIN_GAIN_REDUCTION = 20;

    /// <summary>
    /// ファイル再生時の処理ブロック間隔 (ms) です。
    /// </summary>
    public const int FILE_PROCESSING_INTERVAL_MS = 100;

    /// <summary>
    /// UI 関連の色定数です。
    /// </summary>

    public static readonly System.Windows.Media.Color COLOR_BRAND_GREEN = System.Windows.Media.Color.FromRgb(0x88, 0xCC, 0x00);
    public static readonly System.Windows.Media.Color COLOR_BORDER_GRAY = System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44);

    /// <summary>
    /// Win32 メッセージ定数です。
    /// </summary>
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>
    /// SDR ドライバに関連する設定定数です。
    /// </summary>
    public const float SDR_PPM_SCALE = 1e-6f;
    /// <summary>
    /// SDR の周波数切り替えからIQサンプル到達までの推定遅延時間（秒）です。
    /// </summary>
    public const float SDR_FREQUENCY_SWITCH_DELAY_SEC = 0.12f;

    /// <summary>
    /// アプリケーション全体で使用される JSON シリアライズ設定です。
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}
