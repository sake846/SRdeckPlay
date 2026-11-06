namespace SRdeckCore.SignalProcessing;

/// <summary>
/// Streaming Butterworth low-pass applied independently to I and Q. This is
/// intended for RF channel selectivity and is separate from a rate-conversion
/// anti-aliasing filter.
/// </summary>
public sealed class ComplexIqLowPassFilter
{
    private readonly Biquad[] sectionsI;
    private readonly Biquad[] sectionsQ;
    private int sampleRateHz;

    public ComplexIqLowPassFilter(int order = 4)
    {
        ReadOnlySpan<float> qValues = order switch
        {
            4 => [0.5411961f, 1.306563f],
            8 => [0.5097956f, 0.6013449f, 0.8999762f, 2.5629154f],
            _ => throw new ArgumentOutOfRangeException(nameof(order), "Only fourth- and eighth-order filters are supported.")
        };
        sectionsI = new Biquad[qValues.Length];
        sectionsQ = new Biquad[qValues.Length];
        for (int i = 0; i < qValues.Length; i++)
        {
            sectionsI[i] = new Biquad(qValues[i]);
            sectionsQ[i] = new Biquad(qValues[i]);
        }
    }

    public void Configure(int sampleRateHz, double cutoffHz)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (cutoffHz <= 0 || cutoffHz >= sampleRateHz * 0.5)
            throw new ArgumentOutOfRangeException(nameof(cutoffHz));

        this.sampleRateHz = sampleRateHz;
        foreach (var section in sectionsI) section.Configure(sampleRateHz, cutoffHz);
        foreach (var section in sectionsQ) section.Configure(sampleRateHz, cutoffHz);
    }

    public void Process(float inputI, float inputQ, out float outputI, out float outputQ)
    {
        if (sampleRateHz == 0)
            throw new InvalidOperationException("The IQ low-pass filter is not configured.");
        foreach (var section in sectionsI) inputI = section.Process(inputI);
        foreach (var section in sectionsQ) inputQ = section.Process(inputQ);
        outputI = inputI;
        outputQ = inputQ;
    }

    public void Reset()
    {
        foreach (var section in sectionsI) section.Reset();
        foreach (var section in sectionsQ) section.Reset();
    }

    private sealed class Biquad(float q)
    {
        private float b0;
        private float b1;
        private float b2;
        private float a1;
        private float a2;
        private float z1;
        private float z2;

        public void Configure(int sampleRateHz, double cutoffHz)
        {
            double omega = 2 * Math.PI * cutoffHz / sampleRateHz;
            double cosine = Math.Cos(omega);
            double alpha = Math.Sin(omega) / (2 * q);
            double inverseA0 = 1 / (1 + alpha);
            b0 = (float)((1 - cosine) * 0.5 * inverseA0);
            b1 = (float)((1 - cosine) * inverseA0);
            b2 = b0;
            a1 = (float)(-2 * cosine * inverseA0);
            a2 = (float)((1 - alpha) * inverseA0);
            Reset();
        }

        public float Process(float input)
        {
            float output = b0 * input + z1;
            z1 = b1 * input - a1 * output + z2;
            z2 = b2 * input - a2 * output;
            return output;
        }

        public void Reset() => z1 = z2 = 0;
    }
}
