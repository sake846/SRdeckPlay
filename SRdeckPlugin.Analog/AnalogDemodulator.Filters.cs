using System.Numerics;
using System.Runtime.CompilerServices;
using SRdeckPlugin.Contracts;

namespace SRdeckPlugin.Analog;

internal sealed partial class AnalogDemodulator
{
    // Quadrature mixing halves the pilot's peak amplitude. Leave margin below
    // the nominal 8-10% modulation while rejecting weak incidental 19 kHz energy.
    private const float FmPilotAcquireLevel = 0.035f;
    private const float FmPilotReleaseLevel = 0.025f;

    private float DemodulateFmStereoDiff(float mpx)
    {
        AdvanceFmPilotOscillator();
        float cos19 = _fmPilotCosine;
        float sin19 = _fmPilotSine;

        float i19 = mpx * cos19;
        float q19 = -mpx * sin19;

        _fmPilotI += _fmPilotAlpha * (i19 - _fmPilotI);
        _fmPilotQ += _fmPilotAlpha * (q19 - _fmPilotQ);

        float magnitude = MathF.Sqrt(_fmPilotI * _fmPilotI + _fmPilotQ * _fmPilotQ);
        _fmPilotLevel += _fmPilotAlpha * (magnitude - _fmPilotLevel);
        _fmMpxPower += _fmPilotAlpha * (mpx * mpx - _fmMpxPower);

        // Acquire phase before enabling stereo. Bound the PLL to +/-25 Hz so
        // it cannot follow nearby interference, independent of the sample rate.
        float error = MathF.PI;
        if (_fmPilotLevel > FmPilotReleaseLevel)
        {
            error = MathF.Atan2(_fmPilotQ, _fmPilotI);
            _fmPilotStepCorrection = Math.Clamp(error * _fmPilotLoopGain,
                -_fmPilotMaximumStepCorrection, _fmPilotMaximumStepCorrection);
        }
        else
        {
            _fmPilotStepCorrection = 0;
        }

        _fmPilotPhaseErrorPower += _fmPilotAlpha * (error * error - _fmPilotPhaseErrorPower);
        float levelThreshold = IsStereoDetected ? FmPilotReleaseLevel : FmPilotAcquireLevel;
        // An 8% pilot still clears this ratio with full-level (90%) mono audio.
        // Broadband discriminator noise must not qualify on absolute level alone.
        float minimumPowerRatio = IsStereoDetected ? 0.003f : 0.005f;
        float maximumPhaseErrorPower = IsStereoDetected ? 0.09f : 0.04f;
        bool pilotQualified = _fmPilotLevel > levelThreshold &&
            2f * _fmPilotLevel * _fmPilotLevel > minimumPowerRatio * _fmMpxPower &&
            _fmPilotPhaseErrorPower < maximumPhaseErrorPower;
        UpdateFmStereoDetection(pilotQualified);

        float sin38 = 2f * sin19 * cos19;
        float rawDiff = -mpx * sin38 * 2.0f;

        float filteredDiff = ProcessCascade(_audioDiffLowPass, rawDiff);
        return RemoveDcDiff(filteredDiff);
    }

    private void UpdateFmStereoDetection(bool pilotQualified)
    {
        // Count processing samples, not IQ blocks or wall-clock time: qualification
        // must behave identically during live reception, replay and decimation.
        if (pilotQualified)
        {
            _fmStereoMissingSamples = 0;
            if (!IsStereoDetected && ++_fmStereoQualifyingSamples >= _fmStereoAcquireSamples)
            {
                IsStereoDetected = true;
                _fmStereoQualifyingSamples = 0;
            }
        }
        else
        {
            _fmStereoQualifyingSamples = 0;
            if (IsStereoDetected && ++_fmStereoMissingSamples >= _fmStereoReleaseSamples)
            {
                IsStereoDetected = false;
                _fmStereoMissingSamples = 0;
            }
        }
    }

    private void ResetFmStereoDetection()
    {
        _fmPilotCosine = 1f;
        _fmPilotSine = 0;
        _fmPilotStepCorrection = 0;
        _fmPilotOscillatorSamples = 0;
        _fmPilotI = 0;
        _fmPilotQ = 0;
        _fmPilotLevel = 0;
        _fmMpxPower = 0;
        _fmPilotPhaseErrorPower = MathF.PI * MathF.PI;
        _fmStereoQualifyingSamples = 0;
        _fmStereoMissingSamples = 0;
        IsStereoDetected = false;
    }

    private void AdvanceFmPilotOscillator()
    {
        // The PLL correction is limited to a small fraction of a radian. Applying
        // its small-angle rotation to the precomputed 19 kHz step avoids two
        // transcendental calls for every multiplex sample.
        float correction = _fmPilotStepCorrection;
        float correctionCosine = 1f - 0.5f * correction * correction;
        float correctionSine = correction;
        float stepCosine = _fmPilotStepCosine * correctionCosine -
            _fmPilotStepSine * correctionSine;
        float stepSine = _fmPilotStepSine * correctionCosine +
            _fmPilotStepCosine * correctionSine;
        float cosine = _fmPilotCosine;
        float sine = _fmPilotSine;
        _fmPilotCosine = cosine * stepCosine - sine * stepSine;
        _fmPilotSine = sine * stepCosine + cosine * stepSine;
        if ((++_fmPilotOscillatorSamples & 4095) == 0)
        {
            float inverseMagnitude = 1f / MathF.Sqrt(
                _fmPilotCosine * _fmPilotCosine + _fmPilotSine * _fmPilotSine);
            _fmPilotCosine *= inverseMagnitude;
            _fmPilotSine *= inverseMagnitude;
        }
    }

    private float RemoveDcDiff(float input)
    {
        float output = input - _dcDiffPreviousInput + _dcPole * _dcDiffPreviousOutput;
        _dcDiffPreviousInput = input;
        _dcDiffPreviousOutput = output;
        return output;
    }

    private (float Left, float Right) ApplyBroadcastFmDeemphasisStereo(float leftInput, float rightInput)
    {
        _fmDeemphasisL += _fmDeemphasisAlpha * (leftInput - _fmDeemphasisL);
        _fmDeemphasisR += _fmDeemphasisAlpha * (rightInput - _fmDeemphasisR);
        return (_fmDeemphasisL, _fmDeemphasisR);
    }

    private float DemodulateAm(Complex32 sample)
    {
        float magnitude = MathF.Sqrt(sample.I * sample.I + sample.Q * sample.Q);
        if (_amCarrierLevel == 0) _amCarrierLevel = magnitude;
        _amCarrierLevel += _amCarrierAlpha * (magnitude - _amCarrierLevel);
        float normalizationGain = WeakSignalGain(_amCarrierLevel);
        return (magnitude - _amCarrierLevel) * normalizationGain * 1.5f;
    }

    private float DemodulateFm(Complex32 sample)
    {
        if (!_fmHasPrevious)
        {
            _fmPrevious = sample;
            _fmHasPrevious = true;
            return 0;
        }

        float cross = sample.Q * _fmPrevious.I - sample.I * _fmPrevious.Q;
        float dot = sample.I * _fmPrevious.I + sample.Q * _fmPrevious.Q;
        _fmPrevious = sample;
        float discriminator = MathF.Atan2(cross, dot) * _processingSampleRateHz /
            (TwoPi * _fmDeviationHz);

        if (_isAfcEnabled)
        {
            _fmFrequencyOffset += _fmAfcAlpha * (discriminator - _fmFrequencyOffset);
            discriminator -= _fmFrequencyOffset;
        }
        return discriminator;
    }

    private float DemodulateSsb(Complex32 sample)
    {
        float magnitude = MathF.Sqrt(sample.I * sample.I + sample.Q * sample.Q);
        if (_ssbSignalLevel == 0) _ssbSignalLevel = magnitude;
        _ssbSignalLevel += _ssbLevelAlpha * (magnitude - _ssbSignalLevel);

        float cosine = _ssbCosine;
        float sine = _ssbSine;

        float downI = sample.I * cosine + sample.Q * sine;
        float downQ = sample.Q * cosine - sample.I * sine;
        Vector2 filtered = ProcessCascade(_ssbLowPass, new Vector2(downI, downQ));

        float audio = filtered.X * cosine - filtered.Y * sine;
        _ssbCosine = cosine * _ssbStepCosine - sine * _ssbStepSine;
        _ssbSine = sine * _ssbStepCosine + cosine * _ssbStepSine;
        if ((++_ssbOscillatorSamples & 4095) == 0)
        {
            float inverseMagnitude = 1f / MathF.Sqrt(
                _ssbCosine * _ssbCosine + _ssbSine * _ssbSine);
            _ssbCosine *= inverseMagnitude;
            _ssbSine *= inverseMagnitude;
        }
        float normalizationGain = WeakSignalGain(_ssbSignalLevel);
        return audio * normalizationGain * 1.5f;
    }

    // ponytail: A fixed 60 dB ceiling keeps empty-channel noise bounded; use a noise-aware AGC if hardware measurements require more range.
    private static float WeakSignalGain(float level) =>
        MathF.Min(1_024f, 1f / MathF.Max(level, 1e-6f));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Complex32 FilterReceiverChannel(Complex32 sample)
    {
        if (!_hasCustomChannelPassband)
        {
            Vector2 filtered = ProcessCascade(_channelLowPass, new Vector2(sample.I, sample.Q));
            return new Complex32(filtered.X, filtered.Y);
        }

        _channelDownMixer.Mix(sample.I, sample.Q, out float downI, out float downQ);
        Vector2 channel = ProcessCascade(_channelLowPass, new Vector2(downI, downQ));
        _channelUpMixer.Mix(channel.X, channel.Y, out float upI, out float upQ);
        return new Complex32(upI, upQ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Complex32 CompensateCic(Complex32 sample)
    {
        Vector2 filtered = _cicCompensation.Process(new Vector2(sample.I, sample.Q));
        return new Complex32(filtered.X, filtered.Y);
    }

    private void ConfigureCicCompensation(
        int inputSampleRateHz,
        int decimationFactor,
        float passbandEdgeHz)
    {
        if (decimationFactor <= 1) return;

        const int cicStages = 5;
        double numerator = Math.Sin(Math.PI * passbandEdgeHz / _processingSampleRateHz);
        double denominator = decimationFactor * Math.Sin(Math.PI * passbandEdgeHz / inputSampleRateHz);
        double cicMagnitude = Math.Pow(Math.Abs(numerator / denominator), cicStages);
        float edgeGain = (float)Math.Clamp(1d / Math.Max(cicMagnitude, 1e-6), 1d, 2d);

        // An RBJ high-shelf has the square root of its endpoint gain at its
        // corner frequency. Squaring here restores the CIC loss at the channel
        // edge while the following 12th-order low-pass rejects the boosted stopband.
        float shelfGainDb = 40f * MathF.Log10(edgeGain);
        _cicCompensation.ConfigureHighShelf(_processingSampleRateHz, passbandEdgeHz, shelfGainDb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ProcessCascade(Biquad[] sections, float input)
    {
        foreach (Biquad section in sections) input = section.Process(input);
        return input;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 ProcessCascade(Biquad[] sections, Vector2 input)
    {
        foreach (Biquad section in sections) input = section.Process(input);
        return input;
    }

    private float ApplyBroadcastFmDeemphasis(float input)
    {
        _fmDeemphasis += _fmDeemphasisAlpha * (input - _fmDeemphasis);
        return _fmDeemphasis;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Complex32 MixToReceiverBaseband(Complex32 sample)
    {
        if (_frequencyOffsetHz == 0) return sample;

        float cosine = _tunerCosine;
        float sine = _tunerSine;
        var mixed = new Complex32(
            sample.I * cosine + sample.Q * sine,
            sample.Q * cosine - sample.I * sine);
        _tunerCosine = cosine * _tunerStepCosine - sine * _tunerStepSine;
        _tunerSine = sine * _tunerStepCosine + cosine * _tunerStepSine;
        if ((++_tunerOscillatorSamples & 4095) == 0)
        {
            float inverseMagnitude = 1f / MathF.Sqrt(
                _tunerCosine * _tunerCosine + _tunerSine * _tunerSine);
            _tunerCosine *= inverseMagnitude;
            _tunerSine *= inverseMagnitude;
        }
        return mixed;
    }

    private float RemoveDc(float input)
    {
        float output = input - _dcPreviousInput + _dcPole * _dcPreviousOutput;
        _dcPreviousInput = input;
        _dcPreviousOutput = output;
        return output;
    }

    private static int SelectDecimationFactor(int inputSampleRateHz, int bandwidthHz)
    {
        int targetRateHz = Math.Max(96_000, checked(bandwidthHz * 4));
        int maximumFactor = Math.Max(1, inputSampleRateHz / targetRateHz);
        // Keep the intermediate rate integral so that the streaming rational
        // audio resampler has an exact clock and cannot drift over long runs.
        for (int factor = maximumFactor; factor > 1; factor--)
            if (inputSampleRateHz % factor == 0) return factor;
        return 1;
    }

    private static void ConfigureButterworth(Biquad[] sections, float sampleRateHz, float cutoffHz)
    {
        ReadOnlySpan<float> qValues = sections.Length switch
        {
            2 => [0.5411961f, 1.306563f],
            4 => [0.5097956f, 0.6013449f, 0.8999762f, 2.5629154f],
            6 => [0.5043145f, 0.5411961f, 0.6302362f, 0.8213398f, 1.306563f, 3.8306488f],
            _ => throw new ArgumentException("Only fourth-, eighth-, and twelfth-order filters are supported.", nameof(sections))
        };
        for (int index = 0; index < sections.Length; index++)
            sections[index].ConfigureLowPass(sampleRateHz, cutoffHz, qValues[index]);
    }

    private void ResetFilters()
    {
        foreach (Biquad filter in _channelLowPass) filter.Reset();
        foreach (Biquad filter in _audioLowPass) filter.Reset();
        foreach (Biquad filter in _ssbLowPass) filter.Reset();
        foreach (Biquad filter in _audioDiffLowPass) filter.Reset();
        _cicCompensation.Reset();
    }

    internal sealed class Biquad
    {
        private float _b0;
        private float _b1;
        private float _b2;
        private float _a1;
        private float _a2;
        private float _z1;
        private float _z2;
        private Vector2 _iqZ1;
        private Vector2 _iqZ2;

        public void ConfigureLowPass(float sampleRateHz, float cutoffHz, float q)
        {
            cutoffHz = Math.Clamp(cutoffHz, 1f, sampleRateHz * 0.45f);
            float omega = TwoPi * cutoffHz / sampleRateHz;
            float cosine = MathF.Cos(omega);
            float alpha = MathF.Sin(omega) / (2f * q);
            float inverseA0 = 1f / (1f + alpha);
            _b0 = (1f - cosine) * 0.5f * inverseA0;
            _b1 = (1f - cosine) * inverseA0;
            _b2 = _b0;
            _a1 = -2f * cosine * inverseA0;
            _a2 = (1f - alpha) * inverseA0;
            Reset();
        }

        public void ConfigureHighShelf(float sampleRateHz, float cornerHz, float gainDb)
        {
            cornerHz = Math.Clamp(cornerHz, 1f, sampleRateHz * 0.45f);
            float amplitude = MathF.Pow(10f, gainDb / 40f);
            float omega = TwoPi * cornerHz / sampleRateHz;
            float sine = MathF.Sin(omega);
            float cosine = MathF.Cos(omega);
            float alpha = sine * 0.5f * MathF.Sqrt(2f);
            float beta = 2f * MathF.Sqrt(amplitude) * alpha;
            float inverseA0 = 1f / ((amplitude + 1f) - (amplitude - 1f) * cosine + beta);

            _b0 = amplitude * ((amplitude + 1f) + (amplitude - 1f) * cosine + beta) * inverseA0;
            _b1 = -2f * amplitude * ((amplitude - 1f) + (amplitude + 1f) * cosine) * inverseA0;
            _b2 = amplitude * ((amplitude + 1f) + (amplitude - 1f) * cosine - beta) * inverseA0;
            _a1 = 2f * ((amplitude - 1f) - (amplitude + 1f) * cosine) * inverseA0;
            _a2 = ((amplitude + 1f) - (amplitude - 1f) * cosine - beta) * inverseA0;
            Reset();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Process(float input)
        {
            float output = _b0 * input + _z1;
            _z1 = _b1 * input - _a1 * output + _z2;
            _z2 = _b2 * input - _a2 * output;
            return output;
        }

        // I and Q have identical coefficients and independent state. Vector2
        // evaluates both lanes together without changing the scalar operation order.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 Process(Vector2 input)
        {
            Vector2 output = _b0 * input + _iqZ1;
            _iqZ1 = _b1 * input - _a1 * output + _iqZ2;
            _iqZ2 = _b2 * input - _a2 * output;
            return output;
        }

        public void Reset()
        {
            _z1 = _z2 = 0;
            _iqZ1 = _iqZ2 = Vector2.Zero;
        }
    }

}
