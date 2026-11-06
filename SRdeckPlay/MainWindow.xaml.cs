using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SRdeck.Audio;

namespace SRdeckPlay;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private readonly PlaybackEngine player = new(new WaveOutAudioOutput());
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private Recording? recording;
    private DemodulationSettings settings = new("FM", 0, -6250, 6250);
    private CancellationTokenSource? analysisCancellation;
    private CancellationTokenSource? openCancellation;
    private double position;
    private bool updatingScroll, busy, closed, playbackActive;
    private bool playbackSkipActive;
    private float playbackSquelchThresholdDbm = -100;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Timeline.SeekRequested += async value => await SeekAsync(value);
        Timeline.RangeChanged += OnRangeChanged;
        Timeline.SquelchThresholdChanged += OnSquelchThresholdChanged;
        LiveView.BandChanged += OnBandChanged;
        timer.Tick += OnTick;
        timer.Start();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "IQ WAV (*.wav)|*.wav", Title = "アナログ狭帯域IQ録音を開く" };
        if (dialog.ShowDialog(this) == true) OpenRecording(dialog.FileName);
    }

    public async void OpenRecording(string path)
    {
        if (busy || closed) return;
        busy = true;
        openCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        openCancellation = cancellation;
        StatusText.Text = $"録音を開いています: {Path.GetFileName(path)}";
        var cacheProgress = new Progress<string>(fileName =>
        {
            if (!closed && !cancellation.IsCancellationRequested)
                StatusText.Text = $"FFT/RSSI解析中: {fileName}";
        });
        try
        {
            await player.PauseAsync();
            playbackActive = false;
            var loaded = await Task.Run(() =>
            {
                var result = Recording.Open(path);
                result.SpectrumCache = SignalAnalysis.LoadOrCreateCache(result, cancellation.Token, cacheProgress);
                return result;
            }, cancellation.Token);
            if (closed || cancellation.IsCancellationRequested) return;
            recording = loaded;
            position = 0;
            ModeBox.SelectedIndex = loaded.Metadata?.Profile switch { "am" => 0, "ssb" => 2, _ => 1 };
            TuningBox.Text = "0";
            CalibrationBox.Text = "";
            SetPreset(useRecordedBandwidth: true);
            settings = ReadSettings();
            Timeline.Settings = settings;
            LiveView.SampleRate = loaded.SampleRate;
            LiveView.Recording = loaded;
            LiveView.Settings = settings;
            LiveView.SetDefaultFrequencySpan(settings.LowerHz, settings.UpperHz);
            LiveView.Reset();
            FileText.Text = Path.GetFileName(loaded.Segments[0].Path);
            long center = loaded.Metadata?.CenterFrequencyHz ?? 0;
            MetadataText.Text = $"{(center > 0 ? $"{center / 1e6:F6} MHz · " : "")}{loaded.SampleRate / 1000d:0.###} kS/s · {loaded.Segments.Length}ファイル" +
                $"\n録音開始: {loaded.FormatTime(0, true)}";
            DiscontinuityList.ItemsSource = loaded.Discontinuities.Select(d => new DiscontinuityItem(d.Position,
                $"{loaded.FormatTime(d.Position)} ▼ {d.Description}")).ToArray();
            OverviewTitle.Text = $"RECORDING OVERVIEW · {loaded.FormatTime(0, true)} · 水色: スケルチレベル / 橙: 不連続線";
            UpdateCalibrationLabel();
            SettingsPanel.IsEnabled = true;
            Timeline.Reset(loaded);
            UpdateTime();
        }
        catch (Exception exception) { if (!closed) ShowError(exception); }
        finally { busy = false; cancellation.Dispose(); if (openCancellation == cancellation) openCancellation = null; UpdateButtons(); }
    }

    private async void OnPlay(object sender, RoutedEventArgs e) => await PlayAsync();
    private async Task PlayAsync()
    {
        if (recording is null || busy || player.IsPlaying) return;
        busy = true;
        try
        {
            settings = ReadSettings();
            float squelchThresholdDbm = ReadSquelchThreshold();
            playbackSkipActive = SkipPlaybackCheckBox.IsChecked == true;
            playbackSquelchThresholdDbm = squelchThresholdDbm;
            if (position >= recording.Duration) position = 0;
            Timeline.SquelchThresholdDbm = squelchThresholdDbm;
            Timeline.InvalidateVisual();
            await player.StartAsync(recording, position, settings, playbackSkipActive, squelchThresholdDbm);
            playbackActive = true;
            ApplyPlotSettings();
            StatusText.Text = SkipPlaybackCheckBox.IsChecked == true
                ? $"スキップ再生中 · スケルチ {squelchThresholdDbm:F0} dBm"
                : "再生中";
        }
        catch (Exception exception) { ShowError(exception); }
        finally { busy = false; UpdateButtons(); }
    }

    private async void OnPause(object sender, RoutedEventArgs e) => await PauseAsync();
    private async Task PauseAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            bool wasPlaying = player.IsPlaying;
            await player.PauseAsync();
            playbackActive = false;
            if (wasPlaying) position = player.Position;
            StatusText.Text = "一時停止";
            UpdateTime();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { busy = false; UpdateButtons(); }
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await PauseAsync();
        position = 0;
        LiveView.Reset();
        UpdateTime();
        StatusText.Text = "停止";
    }

    private async Task SeekAsync(double value)
    {
        if (recording is null || busy) return;
        busy = true;
        try
        {
            bool playing = player.IsPlaying;
            await player.PauseAsync();
            position = Math.Clamp(value, 0, recording.Duration);
            LiveView.Reset();
            if (playing)
            {
                float threshold = ReadSquelchThreshold();
                playbackSkipActive = SkipPlaybackCheckBox.IsChecked == true;
                playbackSquelchThresholdDbm = threshold;
                Timeline.SquelchThresholdDbm = threshold;
                Timeline.InvalidateVisual();
                await player.StartAsync(recording, position, settings, playbackSkipActive, threshold);
            }
            playbackActive = playing;
            UpdateTime();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { busy = false; UpdateButtons(); }
    }

    private DemodulationSettings ReadSettings()
    {
        if (recording is null) throw new InvalidOperationException("録音を開いてください。");
        string mode = ((ComboBoxItem)ModeBox.SelectedItem).Content.ToString()!;
        if (!int.TryParse(TuningBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tuning) ||
            !int.TryParse(LowerBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lower) ||
            !int.TryParse(UpperBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int upper))
            throw new ArgumentException("周波数はHz単位の整数で入力してください。");
        var result = new DemodulationSettings(mode, tuning, lower, upper);
        result.Validate(recording.SampleRate);
        return result;
    }

    private float ReadSquelchThreshold()
    {
        if (!float.TryParse(SquelchLevelBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float threshold) ||
            !float.IsFinite(threshold) || threshold is < -150 or > 0)
            throw new ArgumentException("スケルチレベルは -150～0 dBmの数値で入力してください。");
        return threshold;
    }

    private void OnSquelchThresholdChanged(float threshold)
    {
        SquelchLevelBox.Text = threshold.ToString("F0", CultureInfo.InvariantCulture);
        playbackSquelchThresholdDbm = threshold;
        player.SetSquelchThresholdDbm(threshold);
    }

    private async void OnBandChanged(int lower, int upper, bool completed)
    {
        settings = settings with { LowerHz = lower, UpperHz = upper };
        LowerBox.Text = lower.ToString(CultureInfo.InvariantCulture);
        UpperBox.Text = upper.ToString(CultureInfo.InvariantCulture);
        Timeline.Settings = settings;
        Timeline.InvalidateVisual();
        if (!completed || busy || recording is null) return;

        bool playing = player.IsPlaying;
        if (playing) await PauseAsync();
        ApplyPlotSettings();
        LiveView.Reset();
        if (playing) await PlayAsync();
        else StatusText.Text = "復調設定を適用しました";
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (busy || recording is null) return;
        try
        {
            var next = ReadSettings();
            float squelchThresholdDbm = ReadSquelchThreshold();
            float? manual = null;
            if (!string.IsNullOrWhiteSpace(CalibrationBox.Text))
            {
                if (!float.TryParse(CalibrationBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float db) ||
                    !float.IsFinite(db) || db is < -300 or > 300)
                    throw new ArgumentException("dBm換算補正は -300～300 dBの数値で入力してください。");
                manual = db;
            }
            bool playing = player.IsPlaying;
            if (playing) await PauseAsync();
            settings = next;
            Timeline.SquelchThresholdDbm = squelchThresholdDbm;
            bool calibrationChanged = recording.ManualCalibrationOffsetDb != manual;
            recording.ManualCalibrationOffsetDb = manual;
            UpdateCalibrationLabel();
            ApplyPlotSettings();
            LiveView.Reset();
            if (playing) await PlayAsync();
            else StatusText.Text = "復調設定を適用しました";
            if (calibrationChanged) OnRangeChanged();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void ApplyPlotSettings()
    {
        Timeline.Settings = settings;
        LiveView.Settings = settings;
        Timeline.InvalidateVisual();
        LiveView.InvalidateVisual();
    }

    private void OnPreset(object sender, RoutedEventArgs e) => SetPreset(false);
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (recording is null || ModeBox.SelectedItem is not ComboBoxItem) return;
        var (lower, upper) = GetPresetBounds(useRecordedBandwidth: false);
        LiveView.SetDefaultFrequencySpan(lower, upper);
    }

    private void SetPreset(bool useRecordedBandwidth)
    {
        if (recording is null) return;
        var (lower, upper) = GetPresetBounds(useRecordedBandwidth);
        LowerBox.Text = lower.ToString(CultureInfo.InvariantCulture);
        UpperBox.Text = upper.ToString(CultureInfo.InvariantCulture);
        LiveView.SetDefaultFrequencySpan(lower, upper);
    }

    private (int Lower, int Upper) GetPresetBounds(bool useRecordedBandwidth)
    {
        Recording source = recording ?? throw new InvalidOperationException("録音を開いてください。");
        string mode = ((ComboBoxItem)ModeBox.SelectedItem).Content.ToString()!;
        int bandwidth = useRecordedBandwidth && source.Metadata?.BandwidthHz is > 0
            ? source.Metadata.BandwidthHz : mode switch { "AM" => 10_000, "FM" => 12_500, _ => 3_000 };
        int limit = source.SampleRate / 2 - 1;
        int lower = mode switch { "USB" => 250, "LSB" => -Math.Min(4_000, bandwidth), _ => -bandwidth / 2 };
        int upper = mode switch { "USB" => Math.Min(4_000, bandwidth), "LSB" => -250, _ => bandwidth / 2 };
        return (Math.Clamp(lower, -limit, limit - 100), Math.Clamp(upper, -limit + 100, limit));
    }

    private async void OnRangeChanged()
    {
        analysisCancellation?.Cancel();
        ShowAllButton.Tag = recording is not null && Math.Abs(Timeline.ViewStart) < 0.01 &&
            Math.Abs(Timeline.ViewEnd - recording.Duration) < 0.01;
        if (recording is null || closed) return;
        var cancellation = new CancellationTokenSource();
        analysisCancellation = cancellation;
        Recording source = recording;
        double start = Timeline.ViewStart, end = Timeline.ViewEnd;
        StatusText.Text = "表示範囲を更新中…";
        updatingScroll = true;
        TimeScroll.Maximum = Math.Max(0, recording.Duration - (end - start));
        TimeScroll.ViewportSize = end - start;
        TimeScroll.SmallChange = (end - start) / 10;
        TimeScroll.LargeChange = (end - start) * 0.8;
        TimeScroll.Value = start;
        updatingScroll = false;
        var progress = new Progress<int>(percent =>
        {
            if (!closed && !cancellation.IsCancellationRequested) StatusText.Text = $"表示範囲の更新: {percent}%";
        });
        try
        {
            await Task.Delay(120, cancellation.Token);
            var overview = await Task.Run(() => SignalAnalysis.Analyze(source, start, end, 1536, cancellation.Token, progress), cancellation.Token);
            if (!cancellation.IsCancellationRequested && !closed)
            {
                Timeline.SetData(overview);
                StatusText.Text = player.IsPlaying ? "再生中" : "解析完了 · クリックして再生位置を選択できます";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!closed && !cancellation.IsCancellationRequested) ShowError(exception); }
        finally { cancellation.Dispose(); if (analysisCancellation == cancellation) analysisCancellation = null; }
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Timeline.Zoom(0.5);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Timeline.Zoom(2);
    private void OnShowAll(object sender, RoutedEventArgs e) { if (recording is not null) Timeline.SetRange(0, recording.Duration); }
    private void OnToggleDropoutLines(object sender, RoutedEventArgs e)
    {
        Timeline.ShowDiscontinuityLines = ShowDiscontinuityLinesToggle.IsChecked == true;
        Timeline.InvalidateVisual();
    }
    private void OnScroll(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingScroll) Timeline.SetRange(e.NewValue, Timeline.ViewEnd - Timeline.ViewStart);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (playbackActive)
        {
            position = player.Position;
            UpdateTime();
            if (!player.IsPlaying && !busy)
            {
                playbackActive = false;
                StatusText.Text = "再生終了";
            }
        }
        if (player.TakeFrame() is { } frame)
        {
            LiveView.AddFrame(frame);
            if (playbackActive && playbackSkipActive && float.IsFinite(frame.RssiDbm))
                StatusText.Text = $"スキップ再生中 · RSSI {frame.RssiDbm:F0} dBm · スケルチ {(frame.SquelchOpen ? "開" : "閉")} · 設定 {playbackSquelchThresholdDbm:F0} dBm";
        }
        if (player.TakeError() is { } error) StatusText.Text = $"再生エラー: {error}";
        UpdateButtons();
    }

    private void UpdateTime()
    {
        TimeText.Text = recording?.FormatTime(position, true) ?? "録音時刻なし";
        DurationText.Text = $"再生位置 {FormatTime(position)} / 保存時間 {FormatTime(recording?.Duration ?? 0)}";
        Timeline.Position = position;
        Timeline.InvalidateVisual();
    }
    private void UpdateCalibrationLabel()
    {
        CalibrationText.Text = recording?.ManualCalibrationOffsetDb is { } db ? $"手動校正: dBm = dBFS {db:+0.##;-0.##;0} dB" :
            recording?.HasRecordedCalibration == true ? "録音時のdBm校正値を使用しています。" :
            "校正値未記録: −80 dBで参考dBm表示。実電力に合わせるには換算補正を入力してください。";
    }
    private sealed record DiscontinuityItem(double Position, string Label);
    private async void OnDiscontinuitySelected(object sender, SelectionChangedEventArgs e)
    {
        if (DiscontinuityList.SelectedItem is not DiscontinuityItem item || busy) return;
        await SeekAsync(item.Position);
        if (item.Position < Timeline.ViewStart || item.Position > Timeline.ViewEnd)
            Timeline.SetRange(item.Position - (Timeline.ViewEnd - Timeline.ViewStart) / 2, Timeline.ViewEnd - Timeline.ViewStart);
    }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff");
    private void UpdateButtons()
    {
        PlayButton.IsEnabled = recording is not null && !busy && !player.IsPlaying;
        PauseButton.IsEnabled = !busy && player.IsPlaying;
        StopButton.IsEnabled = recording is not null && !busy;
    }
    private void ShowError(Exception exception) => StatusText.Text = $"エラー: {exception.Message}";
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) OpenRecording(paths[0]);
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            if (player.IsPlaying) await PauseAsync(); else await PlayAsync();
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            await SeekAsync(position + (e.Key == Key.Left ? -5 : 5));
        }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        closed = true;
        timer.Stop();
        analysisCancellation?.Cancel();
        openCancellation?.Cancel();
        player.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        int captionColor = 0x00222222;
        int textColor = 0x00F0F0F0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
