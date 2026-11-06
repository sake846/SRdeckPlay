using System.Buffers.Binary;
using SRdeck.Audio;
using SRdeckCore.SignalProcessing;
using SRdeckPlugin.Analog;
using SRdeckPlugin.Contracts;

namespace SRdeckPlay;

internal sealed record DemodulationSettings(string Mode, int TuningHz, int LowerHz, int UpperHz)
{
    public void Validate(int sampleRate)
    {
        if (Mode is not ("AM" or "FM" or "USB" or "LSB"))
            throw new ArgumentException("復調方式が不正です。");
        if (LowerHz >= UpperHz || UpperHz - (long)LowerHz < 100 ||
            Math.Abs((long)LowerHz) >= sampleRate / 2 || Math.Abs((long)UpperHz) >= sampleRate / 2 ||
            TuningHz + (long)LowerHz <= -sampleRate / 2 || TuningHz + (long)UpperHz >= sampleRate / 2 ||
            Math.Abs((long)TuningHz) >= sampleRate / 2)
            throw new ArgumentException("帯域幅は100 Hz以上、同調位置と通過帯域は録音の周波数範囲内に指定してください。");
    }
}

internal sealed record PlaybackFrame(double Position, float[] Spectrum, float RssiDbm = float.NaN,
    bool SquelchOpen = true, float[]? SpectrumBeforeFilter = null, int SpectrumSampleRateHz = 0,
    int DiscontinuityEpoch = 0, int SkipEpoch = 0);

internal sealed class PlaybackEngine(IAudioOutput audio) : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private double position;
    private float squelchThresholdDbm = -100;
    private PlaybackFrame? latestFrame;
    private string? error;
    private int discontinuityEpoch, skipEpoch;
    public double Position => Volatile.Read(ref position);
    public bool IsPlaying => worker is { IsCompleted: false };
    public PlaybackFrame? TakeFrame() => Interlocked.Exchange(ref latestFrame, null);
    public string? TakeError() => Interlocked.Exchange(ref error, null);
    public void SetSquelchThresholdDbm(float value) => Volatile.Write(ref squelchThresholdDbm, value);

    public async Task StartAsync(Recording recording, double start, DemodulationSettings settings,
        bool skipSilence = false, float squelchThresholdDbm = -100)
    {
        await PauseAsync();
        settings.Validate(recording.SampleRate);
        Volatile.Write(ref this.squelchThresholdDbm, squelchThresholdDbm);
        Volatile.Write(ref position, Math.Clamp(start, 0, recording.Duration));
        audio.Initialize(48_000, 2);
        audio.Play();
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        worker = Task.Run(async () =>
        {
            try { await RunAsync(recording, settings, skipSilence, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { Interlocked.Exchange(ref error, exception.Message); }
            finally { audio.Stop(); }
        });
    }

    public async Task PauseAsync()
    {
        cancellation?.Cancel();
        if (worker is not null) await worker;
        cancellation?.Dispose();
        cancellation = null;
        worker = null;
        Interlocked.Exchange(ref latestFrame, null);
    }

    private async Task RunAsync(Recording recording, DemodulationSettings settings, bool skipSilence, CancellationToken token)
    {
        using var reader = new RecordingReader(recording);
        reader.Seek((long)(Position * recording.SampleRate));
        var samples = new Complex32[recording.SampleRate / 40];
        var output = new float[4096];
        var pcm = new byte[output.Length * 4];
        var demodulator = new AnalogDemodulator();
        var analyzer = new SpectrumAnalyzer();
        var beforeFilterAnalyzer = new SpectrumAnalyzer();
        string profile = settings.Mode is "USB" or "LSB" ? "ssb" : settings.Mode.ToLowerInvariant();
        int bandwidth = profile == "ssb" ? Math.Max(Math.Abs(settings.LowerHz), Math.Abs(settings.UpperHz)) :
            2 * Math.Max(Math.Abs(settings.LowerHz), Math.Abs(settings.UpperHz));
        var channelSamples = new Complex32[AnalogDemodulator.GetChannelOutputCapacity(
            samples.Length, recording.SampleRate, Math.Max(100, bandwidth))];
        bool squelchOpen = true;
        long squelchClosedAt = -1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            while (audio.GetBufferedBytes() > 48_000 * 4 / 10)
            {
                UpdatePosition();
                await Task.Delay(5, token);
            }
            long sampleStart = reader.Position;
            if (recording.IsDiscontinuityAt(sampleStart))
            {
                Interlocked.Increment(ref discontinuityEpoch);
                demodulator.Reset();
                analyzer = new SpectrumAnalyzer();
                beforeFilterAnalyzer = new SpectrumAnalyzer();
                squelchOpen = true;
                squelchClosedAt = -1;
            }
            int count = reader.Read(samples.AsSpan(0, (int)Math.Min(samples.Length, recording.NextBoundary(sampleStart) - sampleStart)));
            if (count == 0) break;
            float rssiDbm = float.NaN;
            if (skipSilence)
            {
                rssiDbm = MeasureRssiDbm(samples.AsSpan(0, count), recording.PowerOffsetAt(sampleStart));
                float threshold = Volatile.Read(ref squelchThresholdDbm);
                bool open = squelchOpen ? rssiDbm >= threshold - 2 : rssiDbm >= threshold + 2;
                if (squelchOpen && !open)
                {
                    squelchClosedAt = sampleStart;
                    audio.ClearBuffer();
                }
                else if (!squelchOpen && squelchClosedAt >= 0 &&
                    sampleStart - squelchClosedAt >= recording.SampleRate * 3L)
                {
                    audio.ClearBuffer();
                    long? openAt = open ? sampleStart :
                        FindNextSquelchOpen(recording, reader.Position, token);
                    if (openAt is { } nextOpen)
                    {
                        System.Media.SystemSounds.Beep.Play();
                        ResumeAt(Math.Max(0, nextOpen - recording.SampleRate));
                    }
                    else ResumeAt(recording.SampleCount);
                    continue;
                }
                squelchOpen = open;
            }
            float[] spectrumBeforeFilter = beforeFilterAnalyzer.PushAndAnalyze(samples.AsSpan(0, count));
            int produced = demodulator.Process(samples.AsSpan(0, count), output, out int channels,
                recording.SampleRate, profile, Math.Max(100, bandwidth), settings.Mode == "LSB",
                isAfcEnabled: false, frequencyOffsetHz: settings.TuningHz, isStereoEnabled: true,
                channelSamples, Span<Complex32>.Empty, out int channelSampleCount,
                channelLowerHz: settings.LowerHz, channelUpperHz: settings.UpperHz);
            float[] spectrum = analyzer.PushAndAnalyze(channelSamples.AsSpan(0, channelSampleCount));
            float correction = recording.PowerOffsetAt(sampleStart);
            for (int bin = 0; bin < spectrum.Length; bin++)
            {
                spectrum[bin] += correction;
                spectrumBeforeFilter[bin] += correction;
            }
            ShiftSpectrumToTuning(spectrumBeforeFilter, settings.TuningHz, recording.SampleRate);
            int frames = produced / channels;
            if (skipSilence && !squelchOpen) Array.Clear(pcm, 0, frames * 4);
            else for (int i = 0; i < frames; i++)
            {
                WritePcm(i * 4, output[i * channels]);
                WritePcm(i * 4 + 2, output[i * channels + channels - 1]);
            }
            audio.WriteSamples(pcm, 0, frames * 4);
            UpdatePosition();
            Interlocked.Exchange(ref latestFrame, new PlaybackFrame(Position, spectrum, rssiDbm, squelchOpen,
                spectrumBeforeFilter, checked((int)Math.Round(demodulator.DemodulationSampleRateHz)),
                Volatile.Read(ref discontinuityEpoch), Volatile.Read(ref skipEpoch)));
        }
        while (audio.GetBufferedBytes() > 0)
        {
            UpdatePosition();
            await Task.Delay(5, token);
        }
        Volatile.Write(ref position, recording.Duration);

        void ResumeAt(long sample)
        {
            Interlocked.Increment(ref skipEpoch);
            reader.Seek(sample);
            demodulator.Reset();
            analyzer = new SpectrumAnalyzer();
            beforeFilterAnalyzer = new SpectrumAnalyzer();
            squelchOpen = true;
            squelchClosedAt = -1;
            Volatile.Write(ref position, sample / (double)recording.SampleRate);
            Interlocked.Exchange(ref latestFrame, null);
        }

        void UpdatePosition() => Volatile.Write(ref position, Math.Clamp(
            reader.Position / (double)recording.SampleRate - audio.GetBufferedBytes() / (48_000d * 4), 0, recording.Duration));
        void WritePcm(int offset, float value) => BinaryPrimitives.WriteInt16LittleEndian(
            pcm.AsSpan(offset), (short)Math.Clamp((int)(value * 32767), short.MinValue, short.MaxValue));
    }

    private long? FindNextSquelchOpen(Recording recording, long start, CancellationToken cancellationToken)
    {
        using var reader = new RecordingReader(recording);
        reader.Seek(start);
        var samples = new Complex32[recording.SampleRate / 40];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sampleStart = reader.Position;
            int count = reader.Read(samples.AsSpan(0,
                (int)Math.Min(samples.Length, recording.NextBoundary(sampleStart) - sampleStart)));
            if (count == 0) return null;
            float rssiDbm = MeasureRssiDbm(samples.AsSpan(0, count), recording.PowerOffsetAt(sampleStart));
            if (rssiDbm >= Volatile.Read(ref squelchThresholdDbm) + 2) return sampleStart;
        }
    }

    private static float MeasureRssiDbm(ReadOnlySpan<Complex32> samples, float correction)
    {
        double power = 0;
        foreach (var sample in samples) power += (double)sample.I * sample.I + (double)sample.Q * sample.Q;
        return (float)(10 * Math.Log10(Math.Max(power / samples.Length, 1e-30)) + correction);
    }

    private static void ShiftSpectrumToTuning(float[] spectrum, int tuningHz, int sampleRate)
    {
        int shift = (int)Math.Round(tuningHz * (double)spectrum.Length / sampleRate);
        shift = (shift % spectrum.Length + spectrum.Length) % spectrum.Length;
        if (shift == 0) return;
        Array.Reverse(spectrum, 0, shift);
        Array.Reverse(spectrum, shift, spectrum.Length - shift);
        Array.Reverse(spectrum);
    }

    public void Dispose()
    {
        cancellation?.Cancel();
        worker?.GetAwaiter().GetResult();
        cancellation?.Dispose();
        audio.Dispose();
    }
}
