namespace SRdeckCore.SignalProcessing;

/// <summary>
/// Streaming complex FIR equalizer that compensates the pass-band droop of a
/// normalized CIC decimator. The response is capped outside the useful band;
/// callers should still apply their channel low-pass filter afterwards.
/// </summary>
public sealed class CicCompensationFilter
{
    public const int TapCount = 17;
    private const int ResponsePoints = 1024;
    private const double MaximumGain = 2d;

    private readonly float[] taps = new float[TapCount];
    private readonly float[] historyI = new float[TapCount];
    private readonly float[] historyQ = new float[TapCount];
    private int position;
    private bool configured;
    private bool identity;

    public double GroupDelayOutputSamples => identity ? 0 : (TapCount - 1) * 0.5;

    public void Configure(int decimationFactor, int cicStages)
    {
        if (decimationFactor <= 0) throw new ArgumentOutOfRangeException(nameof(decimationFactor));
        if (cicStages <= 0) throw new ArgumentOutOfRangeException(nameof(cicStages));

        if (decimationFactor == 1)
        {
            Array.Clear(taps);
            taps[0] = 1f;
            identity = true;
            configured = true;
            Reset();
            return;
        }

        int half = ResponsePoints / 2;
        identity = false;
        var response = new double[half + 1];
        for (int bin = 0; bin <= half; bin++)
        {
            double normalizedOutputFrequency = bin / (double)ResponsePoints;
            double numerator = Math.Sin(Math.PI * normalizedOutputFrequency);
            double denominator = decimationFactor * Math.Sin(
                Math.PI * normalizedOutputFrequency / decimationFactor);
            double cicMagnitude = bin == 0 ? 1d : Math.Pow(Math.Abs(numerator / denominator), cicStages);
            response[bin] = Math.Min(MaximumGain, 1d / Math.Max(cicMagnitude, 1e-9));
        }

        int center = (TapCount - 1) / 2;
        for (int tap = 0; tap < TapCount; tap++)
        {
            int offset = tap - center;
            double coefficient = response[0] + response[half] * Math.Cos(Math.PI * offset);
            for (int bin = 1; bin < half; bin++)
                coefficient += 2d * response[bin] * Math.Cos(
                    2d * Math.PI * bin * offset / ResponsePoints);
            taps[tap] = (float)(coefficient / ResponsePoints);
        }

        double dcGain = taps.Sum(value => (double)value);
        for (int tap = 0; tap < TapCount; tap++) taps[tap] /= (float)dcGain;
        configured = true;
        Reset();
    }

    public void Process(float inputI, float inputQ, out float outputI, out float outputQ)
    {
        if (!configured) throw new InvalidOperationException("The CIC compensation filter is not configured.");
        if (identity)
        {
            outputI = inputI;
            outputQ = inputQ;
            return;
        }
        if (++position == TapCount) position = 0;
        historyI[position] = inputI;
        historyQ[position] = inputQ;

        float sumI = 0;
        float sumQ = 0;
        int last = TapCount - 1;
        int pairCount = last / 2;
        for (int tap = 0; tap < pairCount; tap++)
        {
            int delayed = position - (last - tap);
            if (delayed < 0) delayed += TapCount;
            sumI += taps[tap] * (historyI[position - tap < 0 ? position - tap + TapCount : position - tap] +
                historyI[delayed]);
            sumQ += taps[tap] * (historyQ[position - tap < 0 ? position - tap + TapCount : position - tap] +
                historyQ[delayed]);
        }
        int middle = position - pairCount;
        if (middle < 0) middle += TapCount;
        sumI += taps[pairCount] * historyI[middle];
        sumQ += taps[pairCount] * historyQ[middle];
        outputI = sumI;
        outputQ = sumQ;
    }

    public void Reset()
    {
        Array.Clear(historyI);
        Array.Clear(historyQ);
        position = -1;
    }
}
