using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Sdk;

internal sealed class PackedIqRing
{
    private readonly int durationSeconds;
    private short[] raw = [];
    private int sampleRateHz;
    private int writeSample;
    private int sampleCount;

    public PackedIqRing(int durationSeconds) =>
        this.durationSeconds = Math.Clamp(durationSeconds, 1, 180);

    public void Reset() => writeSample = sampleCount = 0;

    public void Write(ReadOnlySpan<Complex32> samples, int inputSampleRateHz)
    {
        if (inputSampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        EnsureConfigured(inputSampleRateHz);
        int capacity = raw.Length / 2;
        foreach (Complex32 sample in samples)
        {
            int offset = writeSample * 2;
            raw[offset] = Quantize(sample.I);
            raw[offset + 1] = Quantize(sample.Q);
            if (++writeSample == capacity) writeSample = 0;
            if (sampleCount < capacity) sampleCount++;
        }
    }

    public PackedIqHistorySnapshot? TakeSnapshot()
    {
        if (sampleCount == 0 || sampleRateHz <= 0) return null;
        var result = new short[sampleCount * 2];
        int capacity = raw.Length / 2;
        int start = (writeSample - sampleCount + capacity) % capacity;
        int firstSamples = Math.Min(sampleCount, capacity - start);
        Array.Copy(raw, start * 2, result, 0, firstSamples * 2);
        if (firstSamples < sampleCount)
            Array.Copy(raw, 0, result, firstSamples * 2, (sampleCount - firstSamples) * 2);
        return new PackedIqHistorySnapshot(
            sampleRateHz, result, sampleCount / (double)sampleRateHz);
    }

    private void EnsureConfigured(int inputSampleRateHz)
    {
        if (inputSampleRateHz == sampleRateHz && raw.Length != 0) return;
        sampleRateHz = inputSampleRateHz;
        raw = new short[checked(inputSampleRateHz * durationSeconds * 2)];
        Reset();
    }

    private static short Quantize(float value)
    {
        if (!float.IsFinite(value)) return 0;
        return value <= -1f ? (short)-short.MaxValue :
            value >= 1f ? short.MaxValue : (short)(value * short.MaxValue);
    }
}
