using System.Numerics;
using SRdeckPlugin.Contracts;
using static SRdeckPlugin.Sdk.Tetra.TetraPhysicalLayer;

namespace SRdeckPlugin.Sdk.Tetra;

/// <summary>
/// Bounded streaming TETRA downlink receiver. Correlates the normal and synchronization
/// training sequences after RRC filtering and differential demodulation, then
/// re-filters at the estimated carrier and fits frequency across the known symbols.
/// </summary>
public sealed class TetraReceiver
{
    private const double MinimumSyncQuality = 0.94;
    private const double MinimumAcquisitionQuality = 0.90;
    private const int HistoryLength = 4096;
    private static readonly double[] taps = CreateRootRaisedCosine();
    private static readonly Complex[][] syncReferences = SyncPatterns.Select(pattern =>
        Enumerable.Range(0, pattern.SyncSymbols).Select(k => Complex.Conjugate(Map(SyncDibit(pattern, k)))).ToArray()).ToArray();
    private readonly double[] delayI = new double[taps.Length * 2], delayQ = new double[taps.Length * 2];
    private readonly Complex[] matched = new Complex[HistoryLength];
    private readonly Complex[] differential = new Complex[HistoryLength];
    private readonly double[] rawI = new double[HistoryLength], rawQ = new double[HistoryLength];
    private int delayIndex;
    private long samplesSeen, origin, nextOutput, suppressThrough = long.MinValue;
    private Candidate? pending;
    private IqBlockMetadata? timeAnchor;
    private long pendingDeadline;
    public double InputRms { get; private set; }
    public long RejectedCandidates { get; private set; }
    public Complex32[] LastConstellation { get; private set; } = [];
    private readonly record struct Candidate(long Start, int PatternIndex, double Fraction, double CarrierOmega,
        Complex Rotation, double FrequencyOffsetHz, double SyncQuality, double Evm);

    public void Reset()
    {
        Array.Clear(delayI); Array.Clear(delayQ); Array.Clear(matched); Array.Clear(differential);
        Array.Clear(rawI); Array.Clear(rawQ); delayIndex = 0;
        samplesSeen = origin = nextOutput = 0; suppressThrough = long.MinValue;
        pending = null;
        timeAnchor = null; InputRms = 0; RejectedCandidates = 0; LastConstellation = [];
    }

    public IReadOnlyList<TetraSlot> Process(ReadOnlySpan<Complex32> samples,
        ChannelIqBlockMetadata metadata, bool invertQ = false)
    {
        if (metadata.Configuration.OutputSampleRateHz != SampleRate)
            throw new ArgumentException("TETRA requires a 144 kHz channel.", nameof(metadata));
        if (samples.Length != metadata.SampleCount)
            throw new ArgumentException("Channel sample count mismatch.", nameof(metadata));
        if (samplesSeen > 0 && metadata.OutputSampleStart != nextOutput) Reset();
        if (samplesSeen == 0) { origin = metadata.OutputSampleStart; timeAnchor = metadata.Source; }
        List<TetraSlot>? result = null;
        for (int index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            if (!float.IsFinite(sample.I) || !float.IsFinite(sample.Q))
            {
                Reset(); origin = metadata.OutputSampleStart + index + 1; timeAnchor = metadata.Source;
                continue;
            }
            var input = new Complex(sample.I, invertQ ? -sample.Q : sample.Q);
            int rawIndex = (int)(samplesSeen & (HistoryLength - 1));
            rawI[rawIndex] = input.Real; rawQ[rawIndex] = input.Imaginary;
            InputRms = Math.Sqrt(0.999 * InputRms * InputRms + 0.001 * (input.Real * input.Real + input.Imaginary * input.Imaginary));
            Complex filtered = Filter(input);
            matched[(int)(samplesSeen & (HistoryLength - 1))] = filtered;
            if (samplesSeen >= SamplesPerSymbol)
            {
                Complex diff = filtered * Complex.Conjugate(matched[(int)((samplesSeen - SamplesPerSymbol) & (HistoryLength - 1))]);
                double magnitude = diff.Magnitude;
                differential[(int)(samplesSeen & (HistoryLength - 1))] = magnitude > 1e-12 ? diff / magnitude : Complex.Zero;
            }
            long end = samplesSeen++;
            // Reserve the following sample even when the final timing hypothesis wins.
            long start = end - (SlotSymbols - 1) * SamplesPerSymbol - 1;
            if (start < taps.Length + SamplesPerSymbol || start <= suppressThrough) continue;
            Candidate? candidate = Inspect(start, invertQ);
            if (candidate is { } accepted)
            {
                if (pending is not { } best) { pending = accepted; pendingDeadline = start + SamplesPerSymbol; }
                else if (IsBetter(accepted, best)) pending = accepted;
            }
            if (pending is { } integer && start >= pendingDeadline)
            {
                // Refine only the best integer phase to bound interpolation work.
                Candidate best = integer;
                foreach (double fraction in (ReadOnlySpan<double>)[-0.5, -0.25, 0.25, 0.5])
                {
                    Candidate? refined = InspectTiming(integer.Start, integer.PatternIndex, fraction,
                        Correlate(integer.Start, integer.PatternIndex, fraction), invertQ);
                    if (refined is { } value && IsBetter(value, best)) best = value;
                }
                result ??= new List<TetraSlot>();
                result.Add(CreateSlot(best, metadata, invertQ, out var points));
                LastConstellation = points;
                // Adjacent timing hypotheses must not produce duplicate slots.
                suppressThrough = best.Start + (SlotSymbols - 2) * SamplesPerSymbol;
                pending = null;
            }
        }
        nextOutput = metadata.OutputSampleStart + samples.Length;
        return result is null ? Array.Empty<TetraSlot>() : result;
    }

    private Complex Filter(Complex input)
    {
        if (--delayIndex < 0) delayIndex = taps.Length - 1;
        // Mirroring keeps the FIR window contiguous across the ring boundary.
        delayI[delayIndex] = delayI[delayIndex + taps.Length] = input.Real;
        delayQ[delayIndex] = delayQ[delayIndex + taps.Length] = input.Imaginary;
        Vector<double> sumI = Vector<double>.Zero, sumQ = Vector<double>.Zero;
        int t = 0;
        if (Vector.IsHardwareAccelerated)
            for (; t <= taps.Length - Vector<double>.Count; t += Vector<double>.Count)
            {
                var coefficients = new Vector<double>(taps.AsSpan(t));
                sumI += new Vector<double>(delayI.AsSpan(delayIndex + t)) * coefficients;
                sumQ += new Vector<double>(delayQ.AsSpan(delayIndex + t)) * coefficients;
            }
        double real = Vector.Sum(sumI), imaginary = Vector.Sum(sumQ);
        for (; t < taps.Length; t++)
        {
            real += delayI[delayIndex + t] * taps[t];
            imaginary += delayQ[delayIndex + t] * taps[t];
        }
        return new(real, imaginary);
    }

    private static bool IsBetter(Candidate candidate, Candidate best) =>
        candidate.Evm < best.Evm;

    private Complex At(long start, int symbol, double fraction)
    {
        long position = start + symbol * SamplesPerSymbol;
        if (fraction < 0) { position--; fraction++; }
        Complex first = matched[(int)(position & (HistoryLength - 1))];
        return fraction == 0 ? first : first + fraction * (matched[(int)((position + 1) & (HistoryLength - 1))] - first);
    }

    private Complex DifferentialAt(long start, int symbol, double fraction = 0)
    {
        if (fraction == 0) return differential[(int)((start + symbol * SamplesPerSymbol) & (HistoryLength - 1))];
        Complex diff = At(start, symbol, fraction) * Complex.Conjugate(At(start, symbol - 1, fraction));
        double magnitude = diff.Magnitude;
        return magnitude > 1e-12 ? diff / magnitude : Complex.Zero;
    }

    private Complex Correlate(long start, int patternIndex, double fraction)
    {
        var pattern = SyncPatterns[patternIndex];
        var reference = syncReferences[patternIndex];
        Complex corr = Complex.Zero;
        for (int k = 0; k < pattern.SyncSymbols; k++)
        {
            Complex d = DifferentialAt(start, pattern.SyncSymbolOffset + k, fraction);
            if (d == Complex.Zero) return Complex.Zero;
            corr += d * reference[k];
        }
        return corr;
    }

    private Candidate? Inspect(long start, bool invertQ)
    {
        for (int patternIndex = 0; patternIndex < SyncPatterns.Count; patternIndex++)
        {
            var pattern = SyncPatterns[patternIndex];
            Complex corr = Correlate(start, patternIndex, 0);
            double correlationPower = corr.Real * corr.Real + corr.Imaginary * corr.Imaginary;
            // Acquisition may improve after carrier filtering; emission still requires MinimumSyncQuality.
            if (correlationPower < MinimumAcquisitionQuality * MinimumAcquisitionQuality * pattern.SyncSymbols * pattern.SyncSymbols) continue;
            Candidate? candidate = InspectTiming(start, patternIndex, 0, corr, invertQ);
            // A promising integer phase can fail EVM before fractional timing gets a chance.
            if (candidate is null && correlationPower >= MinimumSyncQuality * MinimumSyncQuality * pattern.SyncSymbols * pattern.SyncSymbols)
                foreach (double fraction in (ReadOnlySpan<double>)[-0.5, -0.25, 0.25, 0.5])
                {
                    Candidate? refined = InspectTiming(start, patternIndex, fraction,
                        Correlate(start, patternIndex, fraction), invertQ);
                    if (refined is { } value && (candidate is not { } best || IsBetter(value, best))) candidate = value;
                }
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private Candidate? InspectTiming(long start, int patternIndex, double fraction, Complex corr, bool invertQ)
    {
        var pattern = SyncPatterns[patternIndex];
        if (corr.Magnitude < MinimumAcquisitionQuality * pattern.SyncSymbols) return null;
        double omega = Math.Atan2(corr.Imaginary, corr.Real);
        if (Math.Abs(omega * SymbolRate / (2 * Math.PI)) > 1800) return null;

        Span<Complex> values = stackalloc Complex[SlotSymbols];
        if (!FilterCarrier(start, fraction, omega, values)) return null;
        double carrierOmega = omega;
        corr = Complex.Zero;
        double phase = 0, slope = 0;
        Complex coarseRotation = Complex.FromPolarCoordinates(1, -omega);
        for (int k = 0; k < pattern.SyncSymbols; k++)
        {
            Complex residual = values[pattern.SyncSymbolOffset + k] * syncReferences[patternIndex][k];
            corr += residual;
            residual *= coarseRotation;
            phase += Math.Atan2(residual.Imaginary, residual.Real);
            slope += (k + 1 - pattern.SyncSymbols / 2.0) * phase;
        }
        // Fit phase across all known symbols instead of letting the two endpoints dominate frequency error.
        int n = pattern.SyncSymbols;
        omega += 12 * slope / (n * (n + 1) * (n + 2));
        double quality = Math.Clamp(corr.Magnitude / n, 0, 1);
        if (quality < MinimumSyncQuality) return null;
        // Average decision residuals over the burst to reduce the short training word's carrier noise.
        Complex initialRotation = Complex.FromPolarCoordinates(1, -omega), residualSum = Complex.Zero;
        foreach (Complex sample in values)
        {
            Complex value = sample * initialRotation;
            residualSum += value * Complex.Conjugate(Map(Slice(value)));
        }
        omega += Math.Atan2(residualSum.Imaginary, residualSum.Real);
        double offsetHz = omega * SymbolRate / (2 * Math.PI);
        if (Math.Abs(offsetHz) > 1500) return null;

        Complex rotation = Complex.FromPolarCoordinates(1, -omega);
        double squaredError = 0;
        bool valid = true;
        for (int k = 0; k < SlotSymbols; k++)
        {
            Complex value = values[k] * rotation;
            if (value == Complex.Zero) { valid = false; break; }
            byte dibit = Slice(value);
            Complex ideal = Map(dibit);
            double errorI = value.Real - ideal.Real, errorQ = value.Imaginary - ideal.Imaginary;
            squaredError += errorI * errorI + errorQ * errorQ;
            if (k >= pattern.SyncSymbolOffset && k < pattern.SyncSymbolOffset + pattern.SyncSymbols &&
                dibit != SyncDibit(pattern, k - pattern.SyncSymbolOffset)) valid = false;
        }
        double evm = Math.Sqrt(squaredError / SlotSymbols);
        if (!valid || evm > 0.25) { RejectedCandidates++; return null; }
        return new(start, patternIndex, fraction, carrierOmega, rotation, invertQ ? -offsetHz : offsetHz, quality, evm);
    }

    private bool FilterCarrier(long start, double fraction, double omega, Span<Complex> values)
    {
        // Center the matched filter on the estimated carrier; output rotation alone leaves ISI.
        Span<double> tapsI = stackalloc double[taps.Length], tapsQ = stackalloc double[taps.Length];
        Complex step = Complex.FromPolarCoordinates(1, omega / SamplesPerSymbol), tapRotation = Complex.One;
        for (int t = 0; t < taps.Length; t++)
        {
            int index = taps.Length - 1 - t;
            tapsI[index] = taps[t] * tapRotation.Real; tapsQ[index] = taps[t] * tapRotation.Imaginary;
            tapRotation *= step;
        }
        if (fraction < 0) { start--; fraction++; }
        Complex previous = AtCarrier(start - SamplesPerSymbol, fraction, tapsI, tapsQ);
        for (int k = 0; k < values.Length; k++)
        {
            Complex current = AtCarrier(start + k * SamplesPerSymbol, fraction, tapsI, tapsQ);
            Complex diff = current * Complex.Conjugate(previous);
            double magnitude = diff.Magnitude;
            if (magnitude <= 1e-12) return false;
            values[k] = diff / magnitude;
            previous = current;
        }
        return true;
    }

    private Complex AtCarrier(long position, double fraction, ReadOnlySpan<double> tapsI, ReadOnlySpan<double> tapsQ)
    {
        Vector<double> sumI = Vector<double>.Zero, sumQ = Vector<double>.Zero;
        double real = 0, imaginary = 0;
        int offset = (int)((position - tapsI.Length + 1) & (HistoryLength - 1)), t = 0;
        while (t < tapsI.Length)
        {
            int next = (offset + 1) & (HistoryLength - 1);
            int count = Math.Min(tapsI.Length - t, HistoryLength - offset);
            if (fraction != 0) count = Math.Min(count, HistoryLength - next);
            int k = 0;
            if (Vector.IsHardwareAccelerated)
                for (; k <= count - Vector<double>.Count; k += Vector<double>.Count)
                {
                    var i = new Vector<double>(rawI.AsSpan(offset + k));
                    var q = new Vector<double>(rawQ.AsSpan(offset + k));
                    if (fraction != 0)
                    {
                        i += (new Vector<double>(rawI.AsSpan(next + k)) - i) * fraction;
                        q += (new Vector<double>(rawQ.AsSpan(next + k)) - q) * fraction;
                    }
                    var ci = new Vector<double>(tapsI[(t + k)..]);
                    var cq = new Vector<double>(tapsQ[(t + k)..]);
                    sumI += i * ci - q * cq;
                    sumQ += i * cq + q * ci;
                }
            for (; k < count; k++)
            {
                double i = rawI[offset + k], q = rawQ[offset + k];
                if (fraction != 0)
                {
                    i += fraction * (rawI[next + k] - i);
                    q += fraction * (rawQ[next + k] - q);
                }
                real += i * tapsI[t + k] - q * tapsQ[t + k];
                imaginary += i * tapsQ[t + k] + q * tapsI[t + k];
            }
            offset = (offset + count) & (HistoryLength - 1);
            t += count;
        }
        return new(real + Vector.Sum(sumI), imaginary + Vector.Sum(sumQ));
    }

    private TetraSlot CreateSlot(Candidate candidate, ChannelIqBlockMetadata metadata, bool invertQ, out Complex32[] points)
    {
        // Materialize bits and presentation data only after choosing the best timing.
        var pattern = SyncPatterns[candidate.PatternIndex];
        Span<Complex> values = stackalloc Complex[SlotSymbols];
        if (!FilterCarrier(candidate.Start, candidate.Fraction, candidate.CarrierOmega, values))
            throw new InvalidOperationException("TETRA candidate samples are no longer available.");
        Span<byte> symbols = stackalloc byte[SlotSymbols];
        points = new Complex32[SlotSymbols];
        for (int k = 0; k < SlotSymbols; k++)
        {
            Complex value = values[k] * candidate.Rotation;
            symbols[k] = Slice(value);
            points[k] = new((float)value.Real, (float)value.Imaginary);
        }
        long outputPosition = origin + candidate.Start + (long)Math.Round(candidate.Fraction, MidpointRounding.AwayFromZero) - (taps.Length - 1) / 2;
        long sourcePosition = metadata.MapOutputToSource(outputPosition);
        var anchor = timeAnchor ?? metadata.Source;
        var time = anchor.UtcTimestamp.AddSeconds((sourcePosition - anchor.AbsoluteSampleStart) /
            (double)anchor.SampleRateHz);
        byte[] control = ExtractControl(symbols, pattern);
        byte[] payload = ExtractPayload(symbols, pattern);
        var slot = new TetraSlot(0, time, metadata.Source.StreamId, sourcePosition,
            metadata.Source.SampleRateHz, metadata.Configuration.ChannelCenterFrequencyHz,
            "下り", pattern.Channel, null, pattern.SyncHex,
            Convert.ToHexString(Pack(symbols)),
            Convert.ToHexString(control), (pattern.ControlSymbols + pattern.SecondControlSymbols) * 2,
            Convert.ToHexString(payload), (pattern.FirstPayloadSymbols + pattern.SecondPayloadSymbols) * 2,
            candidate.FrequencyOffsetHz, candidate.SyncQuality, candidate.Evm, invertQ)
        {
            SlotHexBitOffset = 0,
            SlotHexBitLength = SlotBits
        };
        return slot;
    }
}
