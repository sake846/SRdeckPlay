using System.Runtime.CompilerServices;

namespace SRdeckCore.SignalProcessing;

/// <summary>Phase-continuous complex NCO used to translate an offset channel to zero IF.</summary>
public sealed class ComplexFrequencyTranslator
{
    private const int NormalizationInterval = 4_096;
    private double oscillatorI = 1;
    private double oscillatorQ;
    private double rotationI = 1;
    private double rotationQ;
    private int normalizationCounter;
    private bool bypass = true;

    public double FrequencyOffsetHz { get; private set; }
    public int SampleRateHz { get; private set; }

    /// <summary>
    /// Configures translation of a channel at <paramref name="frequencyOffsetHz"/>
    /// relative to the input center frequency. Existing phase is preserved.
    /// </summary>
    public void Configure(double frequencyOffsetHz, int sampleRateHz)
    {
        if (!double.IsFinite(frequencyOffsetHz))
            throw new ArgumentOutOfRangeException(nameof(frequencyOffsetHz));
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (Math.Abs(frequencyOffsetHz) > sampleRateHz * 0.5)
            throw new ArgumentOutOfRangeException(nameof(frequencyOffsetHz));

        FrequencyOffsetHz = frequencyOffsetHz;
        SampleRateHz = sampleRateHz;
        double step = -2 * Math.PI * frequencyOffsetHz / sampleRateHz;
        rotationI = Math.Cos(step);
        rotationQ = Math.Sin(step);
        bypass = frequencyOffsetHz == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Mix(float inputI, float inputQ, out float outputI, out float outputQ)
    {
        if (bypass)
        {
            outputI = inputI;
            outputQ = inputQ;
            return;
        }

        outputI = (float)(inputI * oscillatorI - inputQ * oscillatorQ);
        outputQ = (float)(inputI * oscillatorQ + inputQ * oscillatorI);
        double nextI = oscillatorI * rotationI - oscillatorQ * rotationQ;
        oscillatorQ = oscillatorI * rotationQ + oscillatorQ * rotationI;
        oscillatorI = nextI;

        if (++normalizationCounter == NormalizationInterval)
        {
            double inverseMagnitude = 1 / Math.Sqrt(
                oscillatorI * oscillatorI + oscillatorQ * oscillatorQ);
            oscillatorI *= inverseMagnitude;
            oscillatorQ *= inverseMagnitude;
            normalizationCounter = 0;
        }
    }

    public void ResetPhase()
    {
        oscillatorI = 1;
        oscillatorQ = 0;
        normalizationCounter = 0;
    }
}
