
namespace SRdeck.Audio;

internal sealed class PcmWaveFormat
{
    public PcmWaveFormat(int sampleRate, int channels, short bitsPerSample = 16)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (bitsPerSample <= 0 || (bitsPerSample % 8) != 0) throw new ArgumentOutOfRangeException(nameof(bitsPerSample));

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        BlockAlign = (short)(channels * (bitsPerSample / 8));
        AverageBytesPerSecond = sampleRate * BlockAlign;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public short BitsPerSample { get; }
    public short BlockAlign { get; }
    public int AverageBytesPerSecond { get; }
}
