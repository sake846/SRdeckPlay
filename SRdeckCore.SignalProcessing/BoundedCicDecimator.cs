using System.Runtime.Intrinsics;

namespace SRdeckCore.SignalProcessing;

/// <summary>
/// Streaming normalized CIC-equivalent decimator implemented as cascaded bounded
/// moving sums, avoiding unbounded integrator growth during long-running reception.
/// </summary>
public sealed class BoundedCicDecimator
{
    private Vector128<double>[] delay = [];
    private Vector128<double>[] sum = [];
    private int factor;
    private int stages;
    private int position;
    private double inverseGain;

    public int DecimationFactor => factor;
    public int StageCount => stages;
    public int GroupDelayInputSamples => stages * (factor - 1) / 2;

    public void Configure(int decimationFactor, int stageCount)
    {
        if (stageCount <= 0) throw new ArgumentOutOfRangeException(nameof(stageCount));
        factor = Math.Max(1, decimationFactor);
        stages = stageCount;
        delay = new Vector128<double>[stages * factor];
        sum = new Vector128<double>[stages];
        position = 0;
        inverseGain = 1d / Math.Pow(factor, stages);
    }

    public bool TryProcess(float inputI, float inputQ, out float outputI, out float outputQ)
    {
        if (stages == 0) throw new InvalidOperationException("The decimator is not configured.");

        // I/Q use the same operations in adjacent lanes, including on SSE2-only
        // CPUs. Vector128 supplies the same arithmetic without hardware intrinsics.
        var value = Vector128.Create((double)inputI, (double)inputQ);
        int delayIndex = position;
        if (stages == 5)
        {
            // ponytail: Keep the fused mixer/CIC callers; introduce a block API only if full-chain measurements justify it.
            ProcessStage(ref value, delayIndex, 0);
            ProcessStage(ref value, delayIndex + factor, 1);
            ProcessStage(ref value, delayIndex + factor * 2, 2);
            ProcessStage(ref value, delayIndex + factor * 3, 3);
            ProcessStage(ref value, delayIndex + factor * 4, 4);
        }
        else
        {
            for (int stage = 0; stage < stages; stage++, delayIndex += factor)
                ProcessStage(ref value, delayIndex, stage);
        }

        if (++position < factor)
        {
            outputI = outputQ = 0;
            return false;
        }

        position = 0;
        outputI = (float)(value.GetElement(0) * inverseGain);
        outputQ = (float)(value.GetElement(1) * inverseGain);
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void ProcessStage(ref Vector128<double> value, int delayIndex, int stage)
    {
        var previous = delay[delayIndex];
        delay[delayIndex] = value;
        value = sum[stage] += value - previous;
    }

    public void Reset()
    {
        Array.Clear(delay);
        Array.Clear(sum);
        position = 0;
    }
}
