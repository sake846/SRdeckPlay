
namespace SRdeck.Audio;

/// <summary>
/// 音声再生デバイスを抽象化するインターフェースです。
/// 特定の音声出力実装への依存を隠蔽します。
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>
    /// 再生を開始します。
    /// </summary>
    void Play();

    /// <summary>
    /// 再生を停止します。
    /// </summary>
    void Stop();

    /// <summary>
    /// 指定された音声サンプルデータを再生バッファに書き込みます。
    /// </summary>
    void WriteSamples(byte[] buffer, int offset, int count);

    /// <summary>
    /// 再生を一時停止または再開します。停止中もバッファへの書き込みは継続できます。
    /// </summary>
    void SetPlaybackPaused(bool paused);

    /// <summary>
    /// バッファに溜まっている未再生のバイト数を取得します。
    /// </summary>
    int GetBufferedBytes();

    /// <summary>
    /// バッファの最大容量（バイト数）を取得します。
    /// </summary>
    int BufferLength { get; }

    /// <summary>
    /// 出力デバイスを初期化します。
    /// </summary>
    void Initialize(int sampleRate, int channels);

    /// <summary>
    /// バッファを強制的にクリアします（遅延解消用）。
    /// </summary>
    void ClearBuffer();

    /// <summary>
    /// バッファに溜まっている未再生データを指定サイズ以下まで削減します。
    /// </summary>
    void TrimBufferedBytes(int targetBytes);
}
