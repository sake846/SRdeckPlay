using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SRdeck.Models;
using SRdeck.Renderers;

namespace SRdeckPlay;

internal static class Plot
{
    public static readonly Brush Background = Brushes.Black;
    public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(180, 230, 50));
    public static readonly Brush Secondary = new SolidColorBrush(Color.FromRgb(210, 210, 210));
    public static readonly Brush SpectrumBeforeFill = new SolidColorBrush(Color.FromArgb(48, 185, 185, 185));
    public static readonly Brush SpectrumBeforeStroke = new SolidColorBrush(Color.FromArgb(190, 205, 205, 205));
    public static readonly Pen Grid = new(new SolidColorBrush(Color.FromRgb(45, 45, 45)), 1);
    public static readonly Pen MajorGrid = new(new SolidColorBrush(Color.FromRgb(70, 70, 70)), 1);
    public static readonly Pen Cursor = new(Brushes.White, 1);
    public static readonly Pen Discontinuity = new(Brushes.Orange, 2);
    public static readonly Pen WaterfallDiscontinuity = new(Brushes.White, 1) { DashStyle = DashStyles.Dash };
    public static readonly Pen Squelch = new(new SolidColorBrush(Color.FromRgb(80, 220, 255)), 1.5) { DashStyle = DashStyles.Dash };
    public static readonly Pen Frame = new(new SolidColorBrush(Color.FromRgb(115, 115, 115)), 1);

    public static void Text(DrawingContext dc, string text, double x, double y, Brush? brush = null,
        double size = 11, bool rightAligned = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Application.Current?.TryFindResource("DisplayFontFamily") as FontFamily ?? new FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, brush ?? Secondary, 1);
        dc.DrawText(formatted, new Point(rightAligned ? x - formatted.Width : x, y));
    }

    public static void LevelGrid(DrawingContext dc, Rect rect)
        => LevelGrid(dc, rect, AppConstants.DEFAULT_GRID_TOP_DB - AppConstants.SPECTRUM_VIEW_RANGE_DB,
            AppConstants.SPECTRUM_VIEW_RANGE_DB);

    public static void LevelGrid(DrawingContext dc, Rect rect, double bottomDb, double rangeDb)
    {
        double topDb = bottomDb + rangeDb;
        for (int db = (int)(Math.Floor(topDb / 10) * 10); db >= bottomDb; db -= 10)
        {
            double y = LevelY(db, rect, bottomDb, rangeDb);
            dc.DrawLine(db % 50 == 0 ? MajorGrid : Grid, new(rect.Left, y), new(rect.Right, y));
            if (rect.Height >= 80 || topDb - db < 10 || db - bottomDb < 10 || rect.Height >= 55 && db % 50 == 0)
                Text(dc, db.ToString(), rect.Left - 2, y - 7, rightAligned: true);
        }
    }

    public static void Trace(DrawingContext dc, ReadOnlySpan<float> values, Rect rect)
        => Trace(dc, values, rect, AppConstants.DEFAULT_GRID_TOP_DB - AppConstants.SPECTRUM_VIEW_RANGE_DB,
            AppConstants.SPECTRUM_VIEW_RANGE_DB);

    public static void Trace(DrawingContext dc, ReadOnlySpan<float> values, Rect rect, double bottomDb, double rangeDb)
        => Trace(dc, values, rect, bottomDb, rangeDb, null, null);

    public static void Trace(DrawingContext dc, ReadOnlySpan<float> values, Rect rect, Brush fillBrush, Brush strokeBrush)
        => Trace(dc, values, rect, AppConstants.DEFAULT_GRID_TOP_DB - AppConstants.SPECTRUM_VIEW_RANGE_DB,
            AppConstants.SPECTRUM_VIEW_RANGE_DB, fillBrush, strokeBrush);

    private static void Trace(DrawingContext dc, ReadOnlySpan<float> values, Rect rect, double bottomDb, double rangeDb,
        Brush? fillBrush, Brush? strokeBrush)
    {
        if (values.Length == 0) return;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new(rect.Left, LevelY(values[0], rect, bottomDb, rangeDb)), false, false);
            for (int i = 1; i < values.Length; i++)
                context.LineTo(new(rect.Left + i * rect.Width / Math.Max(1, values.Length - 1),
                    LevelY(values[i], rect, bottomDb, rangeDb)), true, false);
        }
        geometry.Freeze();
        ColorLUT.GetSpectrumColors(0, out var fill, out var stroke);
        Brush actualFill = fillBrush ?? MakeBrush(fill.R, fill.G, fill.B, fill.A);
        Brush actualStroke = strokeBrush ?? MakeBrush(stroke.R, stroke.G, stroke.B, stroke.A);
        var filled = new StreamGeometry();
        using (var context = filled.Open())
        {
            context.BeginFigure(new(rect.Left, rect.Bottom), true, true);
            for (int i = 0; i < values.Length; i++)
                context.LineTo(new(rect.Left + i * rect.Width / Math.Max(1, values.Length - 1),
                    LevelY(values[i], rect, bottomDb, rangeDb)), true, false);
            context.LineTo(new(rect.Right, rect.Bottom), true, false);
        }
        filled.Freeze();
        dc.DrawGeometry(actualFill, null, filled);
        dc.DrawGeometry(null, new Pen(actualStroke, 1), geometry);

        static Brush MakeBrush(float r, float g, float b, float a) => new SolidColorBrush(Color.FromArgb(
            (byte)(a * 255), (byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
    }

    public static double LevelY(double db, Rect rect, double bottomDb, double rangeDb)
        => rect.Top + Math.Clamp((bottomDb + rangeDb - db) / rangeDb, 0, 1) * rect.Height;

    public static BitmapSource Bitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed class TimelineView : FrameworkElement
{
    private OverviewData? data;
    private BitmapSource? bitmap;
    private BitmapSource? bitmapLeftEdge, bitmapRightEdge;
    private int bitmapWidth, bitmapHeight;
    private int bitmapBiasDb, spectrogramBiasDb;
    private int rssiWheelDelta, spectrogramWheelDelta;
    private double rssiOffsetDb;
    private Point dragOrigin;
    private double dragViewStart, dragViewEnd;
    private bool dragMoved, dragSquelch;
    private HwndSource? hwndSource;
    internal Recording? Recording { get; set; }
    internal DemodulationSettings? Settings { get; set; }
    public bool ShowDiscontinuityLines { get; set; } = true;
    public float SquelchThresholdDbm { get; set; } = -100;
    public double Position { get; set; }
    public double ViewStart { get; private set; }
    public double ViewEnd { get; private set; } = 1;
    public event Action<double>? SeekRequested;
    public event Action? RangeChanged;
    public event Action<float>? SquelchThresholdChanged;

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(this, "録音全体のRSSI、実時刻、不連続位置、横方向ウォーターフォール");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void SetData(OverviewData value)
    {
        data = value;
        bitmap = null;
        bitmapLeftEdge = bitmapRightEdge = null;
        InvalidateVisual();
    }

    internal void Reset(Recording recording)
    {
        Recording = recording;
        data = null;
        bitmap = null;
        bitmapLeftEdge = bitmapRightEdge = null;
        rssiOffsetDb = 0;
        spectrogramBiasDb = 0;
        Position = 0;
        SetRange(0, recording.Duration);
    }

    private void UpdateBitmap(Rect rect)
    {
        if (data is null) return;
        int width = (int)Math.Clamp(Math.Ceiling(rect.Width * (data.End - data.Start) / (ViewEnd - ViewStart)), 1, data.Rssi.Length);
        int bins = data.Spectra.Length / data.Rssi.Length;
        int height = Math.Clamp((int)Math.Ceiling(rect.Height), 1, bins);
        if (bitmap is not null && bitmapWidth == width && bitmapHeight == height && bitmapBiasDb == spectrogramBiasDb) return;
        bitmap = Plot.Bitmap(width, height, PeakPixels(data, width, height, spectrogramBiasDb));
        var leftEdge = new CroppedBitmap(bitmap, new Int32Rect(0, 0, 1, height));
        var rightEdge = new CroppedBitmap(bitmap, new Int32Rect(width - 1, 0, 1, height));
        leftEdge.Freeze();
        rightEdge.Freeze();
        bitmapLeftEdge = leftEdge;
        bitmapRightEdge = rightEdge;
        bitmapWidth = width;
        bitmapHeight = height;
        bitmapBiasDb = spectrogramBiasDb;
    }

    internal static byte[] PeakPixels(OverviewData overview, int width, int height, int biasDb = 0)
    {
        var pixels = new byte[width * height * 4];
        int columns = overview.Rssi.Length;
        int bins = overview.Spectra.Length / columns;
        float weakest = float.PositiveInfinity;
        foreach (float level in overview.Spectra)
            if (float.IsFinite(level)) weakest = Math.Min(weakest, level);
        if (!float.IsFinite(weakest)) weakest = -120;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float peak = float.NegativeInfinity;
                for (int row = y * bins / height; row < (y + 1) * bins / height; row++)
                    for (int col = x * columns / width; col < (x + 1) * columns / width; col++)
                        peak = Math.Max(peak, overview.Spectra[row * columns + col]);
                SignalAnalysis.WriteColor(pixels, (y * width + x) * 4, peak, weakest, biasDb);
            }
        return pixels;
    }

    public void SetRange(double start, double length)
    {
        if (Recording is null) return;
        length = Math.Clamp(length, Math.Min(0.05, Recording.Duration), Recording.Duration);
        ViewStart = Math.Clamp(start, 0, Recording.Duration - length);
        ViewEnd = ViewStart + length;
        InvalidateVisual();
        RangeChanged?.Invoke();
    }

    public void Zoom(double factor, double? anchor = null)
    {
        double center = anchor ?? (ViewStart + ViewEnd) / 2;
        SetRange(center - (center - ViewStart) * factor, (ViewEnd - ViewStart) * factor);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Point point = e.GetPosition(this);
        Rect rssi = GetRssiRect();
        Rect waterfall = GetWaterfallRect(rssi);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || !rssi.Contains(point) && !waterfall.Contains(point))
            Zoom(e.Delta > 0 ? 0.5 : 2, TimeAt(point.X));
        else if (rssi.Contains(point))
            rssiOffsetDb = Math.Clamp(rssiOffsetDb + WheelSteps(e.Delta, ref rssiWheelDelta), -60, 60);
        else
            spectrogramBiasDb = Math.Clamp(spectrogramBiasDb + WheelSteps(e.Delta, ref spectrogramWheelDelta), -120, 120);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || hwndSource == source) return;
        if (hwndSource is not null) hwndSource.RemoveHook(OnWindowMessage);
        hwndSource = source;
        hwndSource.AddHook(OnWindowMessage);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (hwndSource is null) return;
        hwndSource.RemoveHook(OnWindowMessage);
        hwndSource = null;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != AppConstants.WM_MOUSEHWHEEL || Recording is null) return IntPtr.Zero;
        long coordinates = lParam.ToInt64();
        int screenX = unchecked((short)(coordinates & 0xFFFF));
        int screenY = unchecked((short)(coordinates >> 16));
        Point point = PointFromScreen(new Point(screenX, screenY));
        if (!new Rect(RenderSize).Contains(point)) return IntPtr.Zero;
        int delta = unchecked((short)(wParam.ToInt64() >> 16));
        if (delta == 0) return IntPtr.Zero;
        double length = ViewEnd - ViewStart;
        SetRange(ViewStart + delta / 120d * length * 0.1, length);
        handled = true;
        return IntPtr.Zero;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        dragOrigin = e.GetPosition(this);
        dragViewStart = ViewStart;
        dragViewEnd = ViewEnd;
        dragMoved = false;
        dragSquelch = IsNearSquelchLine(dragOrigin);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Point point = e.GetPosition(this);
        bool seek = !dragMoved && !dragSquelch;
        dragMoved = dragSquelch = false;
        ReleaseMouseCapture();
        Cursor = CursorAt(point);
        if (seek) SeekRequested?.Invoke(TimeAt(point.X));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            double deltaX = point.X - dragOrigin.X;
            if (dragSquelch)
                SetSquelchFromY(point.Y);
            else if (dragMoved || Math.Max(Math.Abs(deltaX), Math.Abs(point.Y - dragOrigin.Y)) >=
                     Math.Max(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
            {
                dragMoved = true;
                double length = dragViewEnd - dragViewStart;
                SetRange(dragViewStart - deltaX / Math.Max(1, GetRssiRect().Width) * length, length);
            }
            Cursor = CursorAt(point);
            e.Handled = true;
            return;
        }

        Cursor = CursorAt(point);
    }

    private bool IsNearSquelchLine(Point point)
    {
        Rect rssi = GetRssiRect();
        return rssi.Contains(point) && TryGetSquelchLineY(rssi, out double y) && Math.Abs(point.Y - y) <= 8;
    }

    private Cursor CursorAt(Point point)
    {
        if (IsNearSquelchLine(point)) return Cursors.SizeNS;
        return Cursors.Cross;
    }

    private bool TryGetSquelchLineY(Rect rssi, out double y)
    {
        double bottomDb = RssiBottomDb();
        if (SquelchThresholdDbm < bottomDb || SquelchThresholdDbm > bottomDb + 60)
        {
            y = 0;
            return false;
        }
        y = Plot.LevelY(SquelchThresholdDbm, rssi, bottomDb, 60);
        return true;
    }

    private double RssiBottomDb()
    {
        bool hasData = data is { Rssi.Length: > 0 };
        return (hasData ? data!.Rssi.Min() : -120) + rssiOffsetDb;
    }

    private Rect DataRect(Rect rect)
    {
        OverviewData overview = data!;
        double viewLength = ViewEnd - ViewStart;
        return new(rect.Left + (overview.Start - ViewStart) / viewLength * rect.Width, rect.Top,
            (overview.End - overview.Start) / viewLength * rect.Width, rect.Height);
    }

    private void SetSquelchFromY(double y)
    {
        Rect rssi = GetRssiRect();
        double level = RssiBottomDb() + (rssi.Bottom - Math.Clamp(y, rssi.Top, rssi.Bottom)) / rssi.Height * 60;
        float threshold = (float)Math.Clamp(Math.Round(level), -150, 0);
        if (threshold == SquelchThresholdDbm) return;
        SquelchThresholdDbm = threshold;
        SquelchThresholdChanged?.Invoke(threshold);
        InvalidateVisual();
    }

    private double TimeAt(double x) => ViewStart + Math.Clamp((x - 62) / Math.Max(1, ActualWidth - 78), 0, 1) * (ViewEnd - ViewStart);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Plot.Background, null, new Rect(RenderSize));
        if (ActualWidth < 100 || ActualHeight < 75) return;
        Rect rssi = GetRssiRect();
        Rect waterfall = GetWaterfallRect(rssi);
        bool hasData = data is { Rssi.Length: > 0 };
        double rssiBottom = RssiBottomDb();
        const double rssiRange = 60;
        Plot.LevelGrid(dc, rssi, rssiBottom, rssiRange);
        UpdateBitmap(waterfall);
        if (bitmap is not null && hasData)
        {
            OverviewData overview = data!;
            BitmapSource sourceImage = bitmap;
            BitmapSource leftEdge = bitmapLeftEdge ?? sourceImage;
            BitmapSource rightEdge = bitmapRightEdge ?? sourceImage;
            bool overlaps = overview.End > ViewStart && overview.Start < ViewEnd;
            Rect rssiDataRect = overlaps ? DataRect(rssi) : rssi;
            Rect waterfallDataRect = overlaps ? DataRect(waterfall) : waterfall;
            dc.PushClip(new RectangleGeometry(rssi));
            Plot.Trace(dc, overview.Rssi, rssiDataRect, rssiBottom, rssiRange);
            dc.Pop();
            dc.PushClip(new RectangleGeometry(waterfall));
            if (!overlaps)
                dc.DrawImage(overview.End <= ViewStart ? rightEdge : leftEdge, waterfall);
            else
            {
                dc.DrawImage(sourceImage, waterfallDataRect);
                if (waterfallDataRect.Left > waterfall.Left)
                    dc.DrawImage(leftEdge, new Rect(waterfall.Left, waterfall.Top, waterfallDataRect.Left - waterfall.Left, waterfall.Height));
                if (waterfallDataRect.Right < waterfall.Right)
                    dc.DrawImage(rightEdge, new Rect(waterfallDataRect.Right, waterfall.Top, waterfall.Right - waterfallDataRect.Right, waterfall.Height));
            }
            dc.Pop();
        }
        else Plot.Text(dc, Recording is null ? "IQ WAVを開いてください" : "表示範囲を更新中…", waterfall.Left + 20, waterfall.Top + 4);
        if (TryGetSquelchLineY(rssi, out double squelchY))
        {
            dc.DrawLine(Plot.Squelch, new(rssi.Left, squelchY), new(rssi.Right, squelchY));
            Plot.Text(dc, $"SQL {SquelchThresholdDbm:F0} dBm", rssi.Left + 4, Math.Max(rssi.Top, squelchY - 14), Plot.Squelch.Brush, 10);
        }
        dc.DrawRectangle(null, Plot.Frame, rssi);
        if (Recording is { } recording)
        {
            for (int i = 0; waterfall.Height >= 30 && i <= 2; i++)
            {
                double y = waterfall.Top + i * waterfall.Height / 2;
                double hz = recording.SampleRate * (0.5 - i / 2d);
                Plot.Text(dc, $"{hz / 1000:+0.#;-0.#;0}", waterfall.Left - 2,
                    Math.Clamp(y - 6, waterfall.Top, waterfall.Bottom - 10), rightAligned: true);
            }
            double X(double time) => rssi.Left + (time - ViewStart) / (ViewEnd - ViewStart) * rssi.Width;
            if (ShowDiscontinuityLines)
                foreach (var marker in recording.Discontinuities.Where(d => d.Position >= ViewStart && d.Position <= ViewEnd))
                {
                    double x = X(marker.Position);
                    dc.DrawLine(Plot.Discontinuity, new(x, rssi.Top), new(x, waterfall.Bottom));
                }
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = rssi.Left + rssi.Width * i / 4;
            double seconds = ViewStart + (ViewEnd - ViewStart) * i / 4;
            Plot.Text(dc, Recording?.FormatTime(seconds) ?? "--:--:--", Math.Clamp(x - 35, rssi.Left, rssi.Right - 82), waterfall.Bottom + 3);
        }
        if (Position >= ViewStart && Position <= ViewEnd)
        {
            double x = rssi.Left + (Position - ViewStart) / (ViewEnd - ViewStart) * rssi.Width;
            dc.DrawLine(Plot.Cursor, new(x, rssi.Top), new(x, waterfall.Bottom));
        }
        dc.DrawRectangle(null, Plot.Frame, waterfall);
    }

    private Rect GetRssiRect() => new(62, 4, ActualWidth - 78, 80);

    private Rect GetWaterfallRect(Rect rssi)
        => new(rssi.Left, rssi.Bottom + 4, rssi.Width, Math.Max(1, ActualHeight - rssi.Bottom - 22));

    private static int WheelSteps(int delta, ref int remainder)
    {
        remainder += delta;
        int steps = remainder / 120;
        remainder %= 120;
        return steps;
    }
}

public sealed class LiveSpectrumView : FrameworkElement
{
    private const int Rows = 256;
    private readonly byte[] pixels = new byte[SpectrumAnalyzer.Bins * 4];
    private float[] spectrumBeforeFilter = [];
    private float[] spectrum = [];
    private int spectrumSampleRateHz;
    private WriteableBitmap? bitmap;
    private readonly double[] history = new double[Rows];
    private readonly bool[] skippedRows = new bool[Rows];
    private int historyHead, historyCount;
    private int historyDiscontinuityEpoch, historySkipEpoch;
    private HwndSource? hwndSource;
    private double frequencyHalfSpanHz;
    private double dragHalfSpan;
    private bool? dragLowerBand;
    private bool dragBandChanged;
    public int SampleRate { get; set; } = 48_000;
    internal Recording? Recording { get; set; }
    internal DemodulationSettings? Settings { get; set; }
    public event Action<int, int, bool>? BandChanged;

    public LiveSpectrumView()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        IsManipulationEnabled = true;
        ManipulationDelta += OnManipulationDelta;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AutomationProperties.SetName(this, "再生スペクトラムと周波数ウォーターフォール");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || hwndSource == source) return;
        hwndSource = source;
        hwndSource.AddHook(OnWindowMessage);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (hwndSource is null) return;
        hwndSource.RemoveHook(OnWindowMessage);
        hwndSource = null;
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != AppConstants.WM_MOUSEHWHEEL) return IntPtr.Zero;
        long coordinates = lParam.ToInt64();
        int screenX = unchecked((short)(coordinates & 0xFFFF));
        int screenY = unchecked((short)(coordinates >> 16));
        if (!new Rect(RenderSize).Contains(PointFromScreen(new Point(screenX, screenY)))) return IntPtr.Zero;
        int delta = unchecked((short)(wParam.ToInt64() >> 16));
        if (delta == 0) return IntPtr.Zero;
        ZoomFrequency(delta);
        handled = true;
        return IntPtr.Zero;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        ZoomFrequency(e.Delta);
        e.Handled = true;
    }

    private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        double scaleX = e.DeltaManipulation.Scale.X;
        double scaleY = e.DeltaManipulation.Scale.Y;
        if (scaleX <= 0 || scaleY <= 0) return;
        double scale = Math.Sqrt(scaleX * scaleY);
        if (Math.Abs(scale - 1) < 0.0001) return;
        SetFrequencyHalfSpan(GetHalfSpan() / scale);
        e.Handled = true;
    }

    private void ZoomFrequency(int wheelDelta)
    {
        SetFrequencyHalfSpan(GetHalfSpan() / Math.Pow(1.25, wheelDelta / 120d));
    }

    internal void SetDefaultFrequencySpan(int lowerHz, int upperHz)
    {
        SetFrequencyHalfSpan(DefaultHalfSpan(lowerHz, upperHz));
    }

    private double DefaultHalfSpan(int lowerHz, int upperHz)
        => Math.Min(SampleRate / 2d, Math.Max(1500, Math.Max(Math.Abs(lowerHz), Math.Abs(upperHz)) * 1.3));

    private void SetFrequencyHalfSpan(double halfSpan)
    {
        double nyquist = SampleRate / 2d;
        double minimumHalfSpan = Math.Min(nyquist, Math.Max(1, SampleRate / (double)SpectrumAnalyzer.Bins * 4));
        frequencyHalfSpanHz = Math.Clamp(halfSpan, minimumHalfSpan, nyquist);
        InvalidateVisual();
    }

    private Rect GetFftRect()
    {
        double plotHeight = Math.Max(3, ActualHeight - 43);
        return new Rect(62, 8, ActualWidth - 78, plotHeight / 3);
    }

    private Rect GetWaterfallRect(Rect fft) => new(fft.Left, fft.Bottom + 24, fft.Width, fft.Height * 2);

    private double GetHalfSpan()
    {
        if (frequencyHalfSpanHz <= 0)
        {
            var settings = Settings ?? new DemodulationSettings("FM", 0, -6250, 6250);
            frequencyHalfSpanHz = DefaultHalfSpan(settings.LowerHz, settings.UpperHz);
        }
        return frequencyHalfSpanHz;
    }

    private static double FrequencyAt(double x, Rect fft, double halfSpan)
        => (x - fft.Left) / fft.Width * (2 * halfSpan) - halfSpan;

    private static double FrequencyX(double hz, Rect fft, double halfSpan)
        => fft.Left + (0.5 + hz / (2 * halfSpan)) * fft.Width;

    private static (int First, int Count, Rect Plot) GetVisibleSpectrumRange(
        int sampleRateHz, Rect fft, double halfSpan)
    {
        double visibleHalfSpan = Math.Min(halfSpan, sampleRateHz / 2d);
        int first = Math.Clamp((int)Math.Floor((0.5 - visibleHalfSpan / sampleRateHz) * SpectrumAnalyzer.Bins),
            0, SpectrumAnalyzer.Bins - 1);
        int last = Math.Clamp((int)Math.Ceiling((0.5 + visibleHalfSpan / sampleRateHz) * SpectrumAnalyzer.Bins),
            first + 1, SpectrumAnalyzer.Bins);
        double left = FrequencyX(-visibleHalfSpan, fft, halfSpan);
        double right = FrequencyX(visibleHalfSpan, fft, halfSpan);
        return (first, last - first, new Rect(left, fft.Top, right - left, fft.Height));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var settings = Settings;
        if (Recording is null || settings is null || ActualWidth < 150 || ActualHeight < 140) return;
        Point point = e.GetPosition(this);
        Rect fft = GetFftRect(), waterfall = GetWaterfallRect(fft);
        if (point.Y < fft.Top || point.Y > waterfall.Bottom) return;
        dragHalfSpan = GetHalfSpan();
        double lowerDistance = Math.Abs(point.X - FrequencyX(settings.LowerHz, fft, dragHalfSpan));
        double upperDistance = Math.Abs(point.X - FrequencyX(settings.UpperHz, fft, dragHalfSpan));
        if (Math.Min(lowerDistance, upperDistance) > 8) return;
        dragLowerBand = lowerDistance <= upperDistance;
        dragBandChanged = false;
        Focus();
        CaptureMouse();
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (dragLowerBand is null) return;
        FinishBandDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (dragLowerBand is not null) FinishBandDrag();
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        Rect fft = GetFftRect(), waterfall = GetWaterfallRect(fft);
        if (dragLowerBand is { } draggingLower && e.LeftButton == MouseButtonState.Pressed && Settings is { } settings)
        {
            DemodulationSettings updated = MoveBand(FrequencyAt(point.X, fft, dragHalfSpan), settings, draggingLower);
            if (updated != settings)
            {
                Settings = updated;
                dragBandChanged = true;
                BandChanged?.Invoke(updated.LowerHz, updated.UpperHz, false);
                InvalidateVisual();
            }
            Cursor = Cursors.SizeWE;
            e.Handled = true;
            return;
        }

        if (Settings is { } current && point.Y >= fft.Top && point.Y <= waterfall.Bottom)
        {
            double halfSpan = dragLowerBand is null ? GetHalfSpan() : dragHalfSpan;
            double distance = Math.Min(
                Math.Abs(point.X - FrequencyX(current.LowerHz, fft, halfSpan)),
                Math.Abs(point.X - FrequencyX(current.UpperHz, fft, halfSpan)));
            Cursor = distance <= 8 ? Cursors.SizeWE : Cursors.Cross;
        }
        else Cursor = Cursors.Cross;
    }

    private DemodulationSettings MoveBand(double frequency, DemodulationSettings settings, bool lower)
    {
        double nyquist = SampleRate / 2d;
        double minExclusive = Math.Max(-nyquist, -nyquist - settings.TuningHz);
        double maxExclusive = Math.Min(nyquist, nyquist - settings.TuningHz);
        int minimum = FloorTo100(minExclusive) + 100;
        int maximum = CeilingTo100(maxExclusive) - 100;
        int value = (int)(Math.Round(frequency / 100, MidpointRounding.AwayFromZero) * 100);
        if (lower)
        {
            maximum = FloorTo100(Math.Min(maximum, settings.UpperHz - 100));
            if (minimum > maximum) return settings;
            return settings with { LowerHz = Math.Clamp(value, minimum, maximum) };
        }

        minimum = CeilingTo100(Math.Max(minimum, settings.LowerHz + 100));
        if (minimum > maximum) return settings;
        return settings with { UpperHz = Math.Clamp(value, minimum, maximum) };
    }

    private static int FloorTo100(double value) => (int)(Math.Floor(value / 100) * 100);
    private static int CeilingTo100(double value) => (int)(Math.Ceiling(value / 100) * 100);

    private void FinishBandDrag()
    {
        dragLowerBand = null;
        dragHalfSpan = 0;
        if (dragBandChanged && Settings is { } settings)
            BandChanged?.Invoke(settings.LowerHz, settings.UpperHz, true);
        dragBandChanged = false;
        InvalidateVisual();
    }

    internal void Reset()
    {
        historyHead = historyCount = 0;
        Array.Clear(skippedRows);
        spectrumBeforeFilter = [];
        spectrum = [];
        spectrumSampleRateHz = 0;
        InvalidateVisual();
    }

    internal void AddFrame(PlaybackFrame frame)
    {
        bool skipped = frame.SkipEpoch != historySkipEpoch;
        if (historyCount > 0)
        {
            double previousPosition = history[historyHead];
            bool crossedDiscontinuity = frame.DiscontinuityEpoch != historyDiscontinuityEpoch;
            if (frame.Position < previousPosition && !crossedDiscontinuity) Reset();
        }
        historyDiscontinuityEpoch = frame.DiscontinuityEpoch;
        historySkipEpoch = frame.SkipEpoch;
        spectrum = frame.Spectrum;
        spectrumBeforeFilter = frame.SpectrumBeforeFilter ?? [];
        spectrumSampleRateHz = frame.SpectrumSampleRateHz > 0 ? frame.SpectrumSampleRateHz : SampleRate;
        historyHead = (historyHead + Rows - 1) % Rows;
        history[historyHead] = frame.Position;
        skippedRows[historyHead] = skipped && historyCount > 0;
        historyCount = Math.Min(historyCount + 1, Rows);
        for (int i = 0; i < spectrum.Length; i++)
            SignalAnalysis.WriteColor(pixels, i * 4, spectrum[i]);
        bitmap ??= new WriteableBitmap(SpectrumAnalyzer.Bins, Rows, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, historyHead, SpectrumAnalyzer.Bins, 1), pixels, pixels.Length, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Plot.Background, null, new Rect(RenderSize));
        if (ActualWidth < 150 || ActualHeight < 140) return;
        var fft = GetFftRect();
        var waterfall = GetWaterfallRect(fft);
        var settings = Settings ?? new DemodulationSettings("FM", 0, -6250, 6250);
        double halfSpan = dragLowerBand is null ? GetHalfSpan() : dragHalfSpan;
        double X(double hz) => FrequencyX(hz, fft, halfSpan);
        double left = X(settings.LowerHz), right = X(settings.UpperHz);
        var excludedBand = new SolidColorBrush(Color.FromArgb(28, 180, 230, 50));
        double leftShadeEnd = Math.Clamp(left, fft.Left, fft.Right);
        double rightShadeStart = Math.Clamp(right, fft.Left, fft.Right);
        if (leftShadeEnd > fft.Left)
            dc.DrawRectangle(excludedBand, null, new Rect(fft.Left, fft.Top, leftShadeEnd - fft.Left, fft.Height));
        if (rightShadeStart < fft.Right)
            dc.DrawRectangle(excludedBand, null, new Rect(rightShadeStart, fft.Top, fft.Right - rightShadeStart, fft.Height));
        Plot.LevelGrid(dc, fft);
        var steps = RenderUtils.GetFrequencyGridSteps(halfSpan * 2);
        for (double hz = Math.Ceiling(-halfSpan / steps.MainHz) * steps.MainHz; hz <= halfSpan; hz += steps.MainHz)
        {
            double x = X(hz);
            dc.DrawLine(Plot.MajorGrid, new(x, fft.Top), new(x, fft.Bottom));
            Plot.Text(dc, $"{hz / 1000:+0.##;-0.##;0}k", Math.Clamp(x - 17, fft.Left, fft.Right - 40), fft.Bottom + 4);
        }
        var rawRange = GetVisibleSpectrumRange(SampleRate, fft, halfSpan);
        var filteredRange = GetVisibleSpectrumRange(
            spectrumSampleRateHz > 0 ? spectrumSampleRateHz : SampleRate, fft, halfSpan);
        if (bitmap is not null && historyCount > 0)
        {
            // The circular image needs at most two clipped draws; only its newest row is uploaded.
            int firstRows = Math.Min(historyCount, Rows - historyHead);
            DrawRows(historyHead, firstRows, 0);
            if (historyCount > firstRows) DrawRows(0, historyCount - firstRows, firstRows);

            void DrawRows(int sourceRow, int count, int destinationRow)
            {
                double rowHeight = waterfall.Height / Rows;
                double binWidth = filteredRange.Plot.Width / filteredRange.Count;
                dc.PushClip(new RectangleGeometry(new Rect(filteredRange.Plot.Left,
                    waterfall.Top + destinationRow * rowHeight, filteredRange.Plot.Width, count * rowHeight)));
                dc.DrawImage(bitmap, new Rect(filteredRange.Plot.Left - filteredRange.First * binWidth,
                    waterfall.Top + (destinationRow - sourceRow) * rowHeight,
                    SpectrumAnalyzer.Bins * binWidth, waterfall.Height));
                dc.Pop();
            }
        }
        if (spectrumBeforeFilter.Length > 0)
            Plot.Trace(dc, spectrumBeforeFilter.AsSpan(rawRange.First, rawRange.Count), rawRange.Plot,
                Plot.SpectrumBeforeFill, Plot.SpectrumBeforeStroke);
        if (spectrum.Length > 0)
            Plot.Trace(dc, spectrum.AsSpan(filteredRange.First, filteredRange.Count), filteredRange.Plot);
        for (int row = 0; row < historyCount; row++)
        {
            if (!skippedRows[(historyHead + row) % Rows]) continue;
            double y = waterfall.Top + (row + 0.5) * waterfall.Height / Rows;
            dc.DrawLine(Plot.WaterfallDiscontinuity, new(waterfall.Left, y), new(waterfall.Right, y));
        }
        foreach (double x in new[] { left, right })
            dc.DrawLine(new Pen(Plot.Accent, 1), new(x, fft.Top), new(x, fft.Bottom));
        if (left >= fft.Left && left <= fft.Right)
            Plot.Text(dc, $"下限 {settings.LowerHz:+0;-0;0} Hz", Math.Clamp(left - 5, fft.Left + 100, fft.Right),
                fft.Top + 5, Plot.Accent, rightAligned: true);
        if (right >= fft.Left && right <= fft.Right)
            Plot.Text(dc, $"上限 {settings.UpperHz:+0;-0;0} Hz", Math.Clamp(right + 5, fft.Left, fft.Right - 115), fft.Top + 5, Plot.Accent);
        if (historyCount > 0 && Recording is { } source)
        {
            double newest = history[historyHead];
            double oldest = history[(historyHead + historyCount - 1) % Rows];
            DrawDiscontinuities();
            double lastTimeLabelY = double.PositiveInfinity;
            const long timeStepTicks = 5 * TimeSpan.TicksPerSecond;
            if (source.UtcAt(oldest) is { } oldestTime && source.UtcAt(newest) is { } newestTime)
            {
                long oldestTicks = oldestTime.UtcDateTime.Ticks;
                long newestTicks = newestTime.UtcDateTime.Ticks;
                long nextTick = (oldestTicks / timeStepTicks + (oldestTicks % timeStepTicks == 0 ? 0 : 1)) * timeStepTicks;
                for (int olderRow = historyCount - 1; olderRow > 0 && nextTick <= newestTicks; olderRow--)
                {
                    DateTimeOffset? olderTime = source.UtcAt(history[(historyHead + olderRow) % Rows]);
                    DateTimeOffset? newerTime = source.UtcAt(history[(historyHead + olderRow - 1) % Rows]);
                    if (olderTime is not { } older || newerTime is not { } newer || newer <= older) continue;
                    long olderTicks = older.UtcDateTime.Ticks;
                    long newerTicks = newer.UtcDateTime.Ticks;
                    while (nextTick >= olderTicks && nextTick <= newerTicks)
                    {
                        double fraction = (nextTick - olderTicks) / (double)(newerTicks - olderTicks);
                        DrawTime(olderRow - fraction, new DateTimeOffset(nextTick, TimeSpan.Zero).ToLocalTime().ToString("HH:mm:ss"));
                        nextTick += timeStepTicks;
                    }
                }
                if (historyCount == 1 && newestTicks % timeStepTicks == 0)
                    DrawTime(0, newestTime.ToLocalTime().ToString("HH:mm:ss"));
            }
            else
            {
                for (double tick = Math.Ceiling((oldest - 1e-9) / 5) * 5; tick <= newest + 1e-9; tick += 5)
                {
                    for (int row = 0; row < historyCount; row++)
                    {
                        double newerPosition = history[(historyHead + row) % Rows];
                        if (Math.Abs(newerPosition - tick) < 1e-9)
                        {
                            DrawTime(row, $"{(int)tick / 3600:00}:{(int)tick / 60 % 60:00}:{(int)tick % 60:00}");
                            break;
                        }
                        if (row + 1 >= historyCount) continue;
                        double olderPosition = history[(historyHead + row + 1) % Rows];
                        if (newerPosition > tick && olderPosition < tick)
                        {
                            double rowPosition = row + (newerPosition - tick) / (newerPosition - olderPosition);
                            DrawTime(rowPosition, $"{(int)tick / 3600:00}:{(int)tick / 60 % 60:00}:{(int)tick % 60:00}");
                            break;
                        }
                    }
                }
            }

            void DrawTime(double row, string label)
            {
                double y = waterfall.Top + row * waterfall.Height / Rows;
                if (lastTimeLabelY - y < 14) return;
                Plot.Text(dc, label, waterfall.Left + 4, y, Brushes.White, 10);
                lastTimeLabelY = y;
            }

            void DrawDiscontinuities()
            {
                RecordingDiscontinuity[] markers = source.Discontinuities;
                int low = 0, high = markers.Length;
                double minimumPosition = Math.Min(oldest, newest);
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (markers[middle].Position < minimumPosition) low = middle + 1;
                    else high = middle;
                }

                int markerIndex = low;
                if (historyCount == 1)
                {
                    while (markerIndex < markers.Length && markers[markerIndex].Position <= newest)
                    {
                        if (Math.Abs(markers[markerIndex].Position - newest) < 1e-9) DrawLine(0);
                        markerIndex++;
                    }
                    return;
                }

                for (int olderRow = historyCount - 1; olderRow > 0; olderRow--)
                {
                    double olderPosition = history[(historyHead + olderRow) % Rows];
                    double newerPosition = history[(historyHead + olderRow - 1) % Rows];
                    double pairMaximum = Math.Max(olderPosition, newerPosition);
                    while (markerIndex < markers.Length && markers[markerIndex].Position <= pairMaximum)
                    {
                        double markerPosition = markers[markerIndex++].Position;
                        if (markerPosition < Math.Min(olderPosition, newerPosition)) continue;
                        double fraction = olderPosition == newerPosition ? 0.5 :
                            (markerPosition - olderPosition) / (newerPosition - olderPosition);
                        DrawLine(olderRow - fraction);
                    }
                }

                void DrawLine(double row)
                {
                    double y = waterfall.Top + row * waterfall.Height / Rows;
                    dc.DrawLine(Plot.WaterfallDiscontinuity,
                        new(waterfall.Left, y), new(waterfall.Right, y));
                }
            }
        }
        dc.DrawRectangle(null, Plot.Frame, fft);
        dc.DrawRectangle(null, Plot.Frame, waterfall);
    }
}
