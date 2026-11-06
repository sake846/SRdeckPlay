using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SRdeck.Audio;
using SRdeckPlay;
using SRdeckPlugin.Contracts;
using SRdeckPlugin.Analog;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SRdeckPlay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (args.Contains("--benchmark")) { Benchmark(); return 0; }
            TestRecording(directory);
            TestPassband();
            TestSpectrum();
            string timelinePath = TestRecordingTimeline(directory);
            TestWaterfall(timelinePath);
            TestAudio(directory).GetAwaiter().GetResult();
            TestFilteredSpectrum(directory).GetAwaiter().GetResult();
            if (args.Contains("--audio-smoke")) TestWaveOut();
            TestWindow(timelinePath, args.FirstOrDefault(a => !a.StartsWith("--")));
            Console.WriteLine("PASS: recording, sample-aligned dBm/time/gaps, segment seek, malformed WAV, filtered IQ FFT, peak rendering, AM/FM/USB/LSB audio, playback/pause/EOF, WPF layouts.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Benchmark()
    {
        var samples = Tone(262_144, 1_000_000, 6000);
        var analyzer = new SpectrumAnalyzer(samples.Length);
        Measure("Overview FFT (262144 -> 256 bins)", 30, () => analyzer.Analyze(samples, 256));
        var spectrum = new float[SpectrumAnalyzer.Bins];
        Array.Fill(spectrum, -100);
        var view = new LiveSpectrumView();
        int frame = 0;
        Measure("Waterfall (320 frames)", 320, () => view.AddFrame(new PlaybackFrame(frame++ * 0.04,
            (float[])spectrum.Clone(), SpectrumBeforeFilter: (float[])spectrum.Clone())));
        GC.KeepAlive(view);

        static void Measure(string name, int count, Action action)
        {
            for (int i = 0; i < 5; i++) action();
            long retained = GC.GetTotalMemory(true);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) action();
            stopwatch.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            retained = GC.GetTotalMemory(true) - retained;
            Console.WriteLine($"{name}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms, allocated {allocated:N0} B, retained delta {retained:N0} B");
        }
    }

    private static Complex32[] Tone(int count, int rate, double hz, double amplitude = 0.4) =>
        Enumerable.Range(0, count).Select(i => new Complex32(
            (float)(amplitude * Math.Cos(2 * Math.PI * hz * i / rate)),
            (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate)))).ToArray();

    private static void WriteWav(string path, int rate, Complex32[] samples)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 4);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4);
        writer.Write((short)4); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples.Length * 4);
        foreach (var sample in samples) { writer.Write((short)(sample.I * 32767)); writer.Write((short)(sample.Q * 32767)); }
    }

    private static void TestRecording(string directory)
    {
        for (int i = 1; i <= 2; i++)
        {
            string path = Path.Combine(directory, $"segment-{i}.wav");
            WriteWav(path, 96_000, Tone(4_800, 96_000, 1000, i / 10d));
            File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new RecordingMetadata
            {
                PluginId = "analog", RecordingId = "test", SegmentNumber = i, RecordingSampleStart = (i - 1) * 4_800,
                SampleRateHz = 96_000, SampleCount = 4_800, CenterFrequencyHz = 145_000_000,
                Profile = "am", BandwidthHz = 10_000
            }));
        }
        Recording recording = Recording.Open(Path.Combine(directory, "segment-2.wav"));
        Check(recording.Segments.Length == 2 && recording.SampleCount == 9600, "Segments were not grouped.");
        using (var reader = new RecordingReader(recording))
        {
            reader.Seek(4799);
            var samples = new Complex32[4];
            Check(reader.Read(samples) == 4 && Math.Abs(samples[1].I - 0.2) < 0.001, "Cross-segment seek failed.");
            reader.Seek(recording.SampleCount);
            Check(reader.Read(samples) == 0, "Seek to EOF failed.");
            reader.Seek(0);
            Check(reader.Read(samples) == 4 && samples[0].I > 0.09, "Backward seek failed.");
        }
        var data = SignalAnalysis.Analyze(recording, 0, recording.Duration, 20, CancellationToken.None);
        Check(data.Rssi[^1] > data.Rssi[0] + 5, "RSSI overview did not preserve the level change.");
        Check(data.Rssi.All(float.IsFinite), "Overview has invalid levels.");
        Check(data.Spectra.All(float.IsFinite) && data.Spectra.Distinct().Count() > 20, "Overview waterfall is empty.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Expect<OperationCanceledException>(() => SignalAnalysis.Analyze(recording, 0, 0.1, 20, cancellation.Token));
        string second = Path.Combine(directory, "segment-2.json");
        string metadata = File.ReadAllText(second);
        File.WriteAllText(second, metadata.Replace("\"SegmentNumber\":2", "\"SegmentNumber\":3"));
        Check(Recording.Open(Path.Combine(directory, "segment-1.wav")).Discontinuities.Any(d => d.Description.Contains("分割録音")),
            "Missing segments were not marked.");
        File.WriteAllText(second, metadata.Replace("\"SegmentNumber\":2", "\"SegmentNumber\":1"));
        Expect<InvalidDataException>(() => Recording.Open(Path.Combine(directory, "segment-1.wav")));
        File.WriteAllText(second, metadata);
        string malformed = Path.Combine(directory, "malformed.wav");
        WriteWav(malformed, 96_000, Tone(100, 96_000, 1000));
        using (var file = new BinaryWriter(File.OpenWrite(malformed))) { file.BaseStream.Position = 40; file.Write(399); }
        Expect<InvalidDataException>(() => Recording.Open(malformed));
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void TestPassband()
    {
        foreach (var (low, high, wanted, unwanted) in new[] { (500, 3500, 1500, -1500), (-3500, -500, -1500, 1500) })
        {
            double Power(double hz)
            {
                var signal = Tone(48_000, 96_000, hz);
                int bandwidth = Math.Max(Math.Abs(low), Math.Abs(high));
                var channel = new Complex32[AnalogDemodulator.GetChannelOutputCapacity(
                    signal.Length, 96_000, bandwidth)];
                var audio = new float[signal.Length];
                new AnalogDemodulator().Process(signal, audio, out _, 96_000, "ssb", bandwidth,
                    low < 0, false, 0, true,
                    channel, Span<Complex32>.Empty, out int channelCount,
                    channelLowerHz: low, channelUpperHz: high);
                return channel.Skip(10_000).Take(channelCount - 10_000)
                    .Average(s => s.I * s.I + s.Q * s.Q);
            }
            Check(Power(wanted) > 100 * Power(unwanted), "Asymmetric IQ passband failed to reject the opposite sideband.");
        }
        Expect<ArgumentException>(() => new DemodulationSettings("AM", 0, 1000, -1000).Validate(96_000));
        Expect<ArgumentException>(() => new DemodulationSettings("FM", 40_000, -10_000, 10_000).Validate(96_000));
    }

    private static void TestSpectrum()
    {
        float[] spectrum = new SpectrumAnalyzer().Analyze(Tone(SpectrumAnalyzer.Size, 96_000, 6000));
        int peak = Array.IndexOf(spectrum, spectrum.Max());
        Check(Math.Abs(peak - (SpectrumAnalyzer.Bins / 2 + SpectrumAnalyzer.Bins / 16)) <= 1, "FFT sign/frequency shift is incorrect.");
        Check(Math.Abs(spectrum.Max() - 20 * Math.Log10(0.4)) < 0.1, "FFT level is not calibrated to dBFS.");
        var analyzer = new SpectrumAnalyzer();
        var input = Tone(SpectrumAnalyzer.Size / 3, 96_000, -7000);
        var full = analyzer.Analyze(input);
        Check(Math.Abs(analyzer.Analyze(input, 1)[0] - full.Max()) < 0.0001,
            "Single-bin FFT aggregation lost the peak.");
        var reduced = new float[SignalAnalysis.Bins];
        analyzer.Analyze(input, reduced);
        int group = full.Length / reduced.Length;
        for (int bin = 0; bin < reduced.Length; bin++)
            Check(Math.Abs(reduced[bin] - full.AsSpan(bin * group, group).ToArray().Max()) < 0.0001,
                "Reduced FFT lost a peak or changed its level.");
        analyzer.Analyze(ReadOnlySpan<Complex32>.Empty, reduced);
        Check(reduced.All(db => Math.Abs(db + 120) < 0.0001), "Reused FFT buffers retained previous samples.");
        Expect<ArgumentOutOfRangeException>(() => analyzer.Analyze(input, new float[3]));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) analyzer.Analyze(input, reduced);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Buffered FFT allocated memory per frame.");
        var wide = new SpectrumAnalyzer(262_144).Analyze(Tone(262_144, 1_000_000, -62_500), SignalAnalysis.Bins);
        Check(Array.IndexOf(wide, wide.Max()) == 112 && Math.Abs(wide.Max() - 20 * Math.Log10(0.4)) < 0.1,
            "High-rate overview FFT changed frequency or level.");
        var narrow = new float[SpectrumAnalyzer.Bins * 10];
        Array.Fill(narrow, -160);
        narrow[513 * 10 + 3] = -60;
        var pixels = TimelineView.PeakPixels(new(0, 1, new float[10], narrow), 5, 30);
        Check(pixels.Where((_, i) => i % 4 != 3).Any(c => c > 0), "Overview downsampling lost a narrowband signal.");
    }

    private static void TestWaterfall(string recordingPath)
    {
        var view = new LiveSpectrumView { Width = 600, Height = 811 };
        view.Measure(new Size(600, 811));
        view.Arrange(new Rect(0, 0, 600, 811));
        for (int i = 0; i < 320; i++) Add(i * 0.04, -120 + i % 50);
        CheckRows(row => -120 + (319 - row) % 50, 256);
        view.Reset();
        Add(10, -100);
        CheckRows(_ => -100, 1);
        Add(9, -90);
        CheckRows(_ => -90, 1);
        view.Recording = Recording.Open(recordingPath);
        Add(19, -100);
        Add(20, -80);
        CheckRows(row => row switch { 0 => -80, 1 => -100, _ => -90 }, 3);

        void Add(double position, float level)
        {
            var spectrum = new float[SpectrumAnalyzer.Bins];
            Array.Fill(spectrum, level);
            view.AddFrame(new PlaybackFrame(position, spectrum));
        }

        void CheckRows(Func<int, float> levelAt, int count)
        {
            view.UpdateLayout();
            var image = new RenderTargetBitmap(600, 811, 96, 96, PixelFormats.Pbgra32);
            image.Render(view);
            const int waterfallTop = 288;
            var expected = new byte[4];
            var actual = new byte[4];
            for (int row = 0; row < 256; row++)
            {
                SignalAnalysis.WriteColor(expected, 0, row < count ? levelAt(row) : -120);
                int y = waterfallTop + row * 2 + 1;
                if (row == 255) y--;
                image.CopyPixels(new Int32Rect(350, y, 1, 1), actual, 4, 0);
                Check(actual.AsSpan().SequenceEqual(expected), $"Waterfall row {row} is stale or out of order: {Convert.ToHexString(actual)} != {Convert.ToHexString(expected)}.");
            }
        }
    }

    private static string TestRecordingTimeline(string directory)
    {
        string output = Path.Combine(directory, "timeline");
        var writer = new AnalogNarrowbandIqRecording(output, "fm", 145_000_000, 60_000);
        var start = new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.FromHours(9));
        Guid stream = Guid.NewGuid();
        var samples = Tone(48_000, 48_000, 1000, 0.1);
        for (int second = 0; second < 61; second++)
        {
            int gap = (second >= 20 ? 2 : 0) + (second >= 60 ? 3 : 0);
            var metadata = new IqBlockMetadata(stream, 1, second, (second + gap) * 48_000, 0,
                start.AddSeconds(second + gap), 48_000, 145_000_000, samples.Length, IqInputSource.Sdr,
                second is 20 or 60 ? IqDiscontinuity.SamplesDropped : IqDiscontinuity.None);
            Check(writer.Write(samples, 48_000, metadata, "fm", 145_000_000, 60_000, second < 30 ? -80 : -70) is null,
                "Recorder rejected a valid input block.");
        }
        string path = writer.Finish("test")!;
        Recording recording = Recording.Open(path);
        Check(recording.Segments.Length == 2 && recording.Duration == 61, "Recorder split failed.");
        Check(recording.HasRecordedCalibration && recording.PowerUnit == "dBm", "Recorded calibration was not restored.");
        Check(recording.PowerOffsetAt(0) == -80 && recording.PowerOffsetAt(30 * 48_000) == -70,
            "Gain calibration changes were not restored.");
        Check(recording.UtcAt(19) == start.AddSeconds(19) && recording.UtcAt(20) == start.AddSeconds(22) &&
            recording.UtcAt(61) == start.AddSeconds(66), "Sample-aligned wall clock or gap mapping failed.");
        Check(recording.Discontinuities.Length == 2 && recording.Discontinuities[0].Position == 20 &&
            recording.Discontinuities[1].Position == 60,
            "The precise input-drop marker was lost or duplicated.");
        Check(recording.NextBoundary(19 * 48_000) == 20 * 48_000 && recording.IsDiscontinuityAt(20 * 48_000),
            "Processing boundaries did not preserve a source gap.");
        var overview = SignalAnalysis.Analyze(recording, 0, 0.1, 10, CancellationToken.None);
        Check(overview.Rssi.All(db => Math.Abs(db + 100) < 0.1), "RSSI is not calibrated to dBm.");
        recording.ManualCalibrationOffsetDb = -60;
        Check(recording.PowerOffsetAt(0) == -60 && recording.PowerOffsetAt(40 * 48_000) == -60, "Manual calibration override failed.");

        string partialPath = Path.Combine(directory, "missing-calibration.wav");
        WriteWav(partialPath, 48_000, samples);
        File.WriteAllText(Path.ChangeExtension(partialPath, ".json"), JsonSerializer.Serialize(new RecordingMetadata
        {
            Timeline = [new(0, start, -60, false), new(24_000, start.AddSeconds(0.5), null, false)]
        }));
        var partial = Recording.Open(partialPath);
        Check(!partial.HasRecordedCalibration && partial.PowerOffsetAt(0) == -60 && partial.PowerOffsetAt(36_000) == -80,
            "Missing calibration reused a stale first-segment correction.");
        return path;
    }

    private static async Task TestFilteredSpectrum(string directory)
    {
        const int rate = 96_000;
        var wanted = Tone(rate / 2, rate, 11_000, 0.2);
        var rejected = Tone(rate / 2, rate, 8_000, 0.2);
        for (int i = 0; i < wanted.Length; i++) wanted[i] = new(wanted[i].I + rejected[i].I, wanted[i].Q + rejected[i].Q);
        string path = Path.Combine(directory, "filtered.wav");
        WriteWav(path, rate, wanted);
        using var engine = new PlaybackEngine(new FakeAudio());
        await engine.StartAsync(Recording.Open(path), 0, new("USB", 10_000, 250, 3000));
        var timeout = Stopwatch.StartNew();
        while (engine.IsPlaying && timeout.Elapsed.TotalSeconds < 5) await Task.Delay(5);
        Check(!engine.IsPlaying && engine.TakeError() is null, "Filtered spectrum playback failed.");
        var frame = engine.TakeFrame() ?? throw new InvalidOperationException("Filtered spectrum frame missing.");
        int Bin(double hz) => (int)((0.5 + hz / rate) * SpectrumAnalyzer.Bins);
        float Peak(int center) => frame.Spectrum.Skip(center - 2).Take(5).Max();
        Check(Peak(Bin(1000)) > Peak(Bin(-2000)) + 25, "Live FFT was taken before the IQ passband.");
        Check(Peak(Bin(1000)) < -90 && Peak(Bin(1000)) > -97, "Live FFT did not use dBm correction.");
    }

    private static async Task TestAudio(string directory)
    {
        foreach (var (mode, rate) in new[] { ("AM", 48_000), ("AM", 96_000), ("FM", 48_000), ("FM", 96_000),
                     ("FM", 384_000), ("USB", 48_000), ("USB", 96_000), ("LSB", 48_000), ("LSB", 96_000) })
        {
            var signal = new Complex32[rate / 2];
            for (int i = 0; i < signal.Length; i++)
            {
                double tone = Math.Sin(2 * Math.PI * 1000 * i / rate);
                double phase = mode switch { "FM" => (rate == 384_000 ? 75 : 3) * tone, "LSB" => -2 * Math.PI * 1000 * i / rate, _ => 2 * Math.PI * 1000 * i / rate };
                signal[i] = mode == "AM" ? new((float)(0.4 * (1 + 0.5 * tone)), 0) :
                    new((float)(0.4 * Math.Cos(phase)), (float)(0.4 * Math.Sin(phase)));
            }
            string path = Path.Combine(directory, mode + ".wav");
            WriteWav(path, rate, signal);
            var fake = new FakeAudio();
            using var engine = new PlaybackEngine(fake);
            var settings = mode switch
            {
                "USB" => new DemodulationSettings(mode, 0, 250, 3000),
                "LSB" => new DemodulationSettings(mode, 0, -3000, -250),
                "FM" when rate == 384_000 => new DemodulationSettings(mode, 0, -125_000, 125_000),
                _ => new DemodulationSettings(mode, 0, -6250, 6250)
            };
            Recording recording = Recording.Open(path);
            await engine.StartAsync(recording, 0, settings);
            var timeout = Stopwatch.StartNew();
            while (engine.IsPlaying && timeout.Elapsed.TotalSeconds < 5) await Task.Delay(5);
            Check(!engine.IsPlaying && engine.TakeError() is null, mode + " playback did not finish.");
            Check(engine.Position == recording.Duration && Math.Abs(fake.Samples.Count / 2 - 24_000) < 20, mode + " output rate/duration drifted.");
            double Energy(double hz)
            {
                double real = 0, imaginary = 0;
                for (int frame = 5000; frame < fake.Samples.Count / 2; frame++)
                {
                    real += fake.Samples[frame * 2] * Math.Cos(2 * Math.PI * hz * frame / 48_000);
                    imaginary += fake.Samples[frame * 2] * Math.Sin(2 * Math.PI * hz * frame / 48_000);
                }
                return real * real + imaginary * imaginary;
            }
            Check(Energy(1000) > 100 * Energy(2000), mode + " did not recover the 1 kHz audio.");
            Check(fake.Samples.Skip(10000).Any(s => Math.Abs((int)s) > 100), mode + " output is silent.");
            await engine.StartAsync(recording, 0.25, settings);
            while (engine.IsPlaying) await Task.Delay(5);
            Check(Math.Abs(fake.Samples.Count / 2 - 12_000) < 20, mode + " seek did not shorten output.");
            fake.Hold = true;
            await engine.StartAsync(recording, 0, settings);
            await Task.Delay(40);
            await engine.PauseAsync();
            Check(!engine.IsPlaying && engine.Position < recording.Duration, "Pause did not cancel queued playback.");
        }
    }

    private static void TestWaveOut()
    {
        using var audio = new WaveOutAudioOutput();
        audio.Initialize(48_000, 2);
        audio.Play();
        audio.WriteSamples(new byte[9600], 0, 9600);
        Check(audio.GetBufferedBytes() > 0, "WinMM did not queue silence.");
        var timeout = Stopwatch.StartNew();
        while (audio.GetBufferedBytes() > 0 && timeout.Elapsed.TotalSeconds < 2) Thread.Sleep(5);
        Check(audio.GetBufferedBytes() == 0, "WinMM did not drain queued silence.");
        Console.WriteLine("PASS: Windows waveOut output (silence).");
    }

    private static void TestWindow(string recordingPath, string? previewPath)
    {
        var app = new App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow { Width = 1380, Height = 880 };
        window.Show();
        window.OpenRecording(recordingPath);
        var timeout = Stopwatch.StartNew();
        while (!((Button)window.FindName("PlayButton")).IsEnabled && timeout.Elapsed.TotalSeconds < 5) Pump();
        Check(((Button)window.FindName("PlayButton")).IsEnabled, "WPF recording open failed: " + ((TextBlock)window.FindName("StatusText")).Text);
        while (!((TextBlock)window.FindName("StatusText")).Text.Contains("解析完了") && timeout.Elapsed.TotalSeconds < 5) Pump();
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("解析完了"), "WPF overview analysis did not complete.");
        Check(Math.Abs(((FrameworkElement)window.FindName("ControlPanel")).ActualWidth - 380) < 0.1,
            "Playback controls do not have a fixed width.");
        Check(((FrameworkElement)window.FindName("OverviewPanel")).ActualHeight <= Math.Max(300, window.ActualHeight / 3),
            "Overview exceeded its minimum height and upper-quarter layout.");
        Check(((ListBox)window.FindName("DiscontinuityList")).Items.Count == 2,
            "Discontinuity navigation list is missing.");
        ((ListBox)window.FindName("DiscontinuityList")).SelectedIndex = 0;
        Pump();
        Check(Math.Abs(((TimelineView)window.FindName("Timeline")).Position - 20) < 0.001,
            "Selecting a discontinuity did not seek to it.");
        Check(((TextBlock)window.FindName("TimeText")).Text.Contains("2026"), "Playback clock is still elapsed-only.");
        var timeline = (TimelineView)window.FindName("Timeline");
        double duration = timeline.ViewEnd;
        timeline.Zoom(0.5);
        Check(Math.Abs(timeline.ViewEnd - timeline.ViewStart - duration / 2) < 1e-6, "Time zoom failed.");
        timeline.SetRange(0, duration);
        timeout.Restart();
        while (!((TextBlock)window.FindName("StatusText")).Text.Contains("解析完了") && timeout.Elapsed.TotalSeconds < 5) Pump();
        ((ComboBox)window.FindName("ModeBox")).SelectedIndex = 0;
        ((TextBox)window.FindName("LowerBox")).Text = "-5000";
        ((TextBox)window.FindName("UpperBox")).Text = "5000";
        var live = (LiveSpectrumView)window.FindName("LiveView");
        ((TextBox)window.FindName("CalibrationBox")).Text = "-60";
        ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(live.Recording?.PowerOffsetAt(0) == -60, "UI calibration was not applied.");
        ((TextBox)window.FindName("CalibrationBox")).Text = "";
        ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(live.Recording?.PowerOffsetAt(0) == -80, "UI automatic calibration was not restored.");
        timeout.Restart();
        while (!((TextBlock)window.FindName("StatusText")).Text.Contains("解析完了") && timeout.Elapsed.TotalSeconds < 5) Pump();
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("解析完了"), "Calibration reanalysis did not complete.");
        var analyzer = new SpectrumAnalyzer();
        for (int i = 0; i < 256; i++)
        {
            float[] spectrum = analyzer.Analyze(Tone(SpectrumAnalyzer.Size, 48_000, 1000 + 600 * Math.Sin(i * 0.06)));
            for (int bin = 0; bin < spectrum.Length; bin++) spectrum[bin] -= 70;
            live.AddFrame(new PlaybackFrame(30 + i * 0.05, spectrum));
        }
        window.UpdateLayout();
        Pump();
        if (previewPath is not null)
        {
            SavePreview(previewPath);
        }
        window.Width = 1000;
        window.Height = 720;
        window.UpdateLayout();
        Pump();
        Check(Math.Abs(((FrameworkElement)window.FindName("ControlPanel")).ActualWidth - 380) < 0.1,
            "Control width changed when the window was resized.");
        if (previewPath is not null) SavePreview(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(previewPath))!,
            Path.GetFileNameWithoutExtension(previewPath) + "-compact.png"));
        window.Close();
        app.Shutdown();
        void SavePreview(string path)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.GetFullPath(path));
            encoder.Save(stream);
        }
        static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private sealed class FakeAudio : IAudioOutput
    {
        public List<short> Samples { get; } = [];
        public bool Hold { get; set; }
        public int BufferLength => 48_000;
        public void Initialize(int sampleRate, int channels) { Samples.Clear(); Check(sampleRate == 48_000 && channels == 2, "Wrong output format."); }
        public void WriteSamples(byte[] buffer, int offset, int count)
        {
            for (int i = offset; i < offset + count; i += 2) Samples.Add(BitConverter.ToInt16(buffer, i));
        }
        public int GetBufferedBytes() => Hold ? Samples.Count * 2 : 0;
        public void Play() { }
        public void Stop() { }
        public void SetPlaybackPaused(bool paused) { }
        public void ClearBuffer() { }
        public void TrimBufferedBytes(int targetBytes) { }
        public void Dispose() { }
    }
}
