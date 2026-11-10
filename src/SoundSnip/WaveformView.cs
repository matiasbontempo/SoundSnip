using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoundSnip.Core;

namespace SoundSnip;

/// <summary>
/// Waveform with a time ruler, a draggable trim selection (two handles), a playhead, wheel zoom and panning.
/// It owns only the view window (zoom/scroll); selection and playhead live in the view-model and arrive via properties.
/// Pointer handling is exposed as plain methods (PointerDown/Move/Up, Wheel) so it can be tested without a real mouse.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const double RulerHeight = 24;
    private const double Pad = 10;          // keeps the handles at 0:00 and the very end fully visible and grabbable
    private const double GrabRadius = 8;
    private const double MinSelection = 0.05;

    // ------------------------------------------------------------------ dependency properties
    private static DependencyProperty Reg<T>(string name, T def, PropertyChangedCallback? cb = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(WaveformView),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender, cb));

    public static readonly DependencyProperty WaveformProperty = Reg<WaveformData?>(nameof(Waveform), null, (d, _) => ((WaveformView)d).OnWaveformChanged());
    public static readonly DependencyProperty SelectionStartProperty = Reg(nameof(SelectionStart), 0.0);
    public static readonly DependencyProperty SelectionEndProperty = Reg(nameof(SelectionEnd), 0.0);
    public static readonly DependencyProperty PlayheadProperty = Reg(nameof(Playhead), 0.0, (d, _) => ((WaveformView)d).OnPlayheadChanged());
    public static readonly DependencyProperty AutoScrollProperty = Reg(nameof(AutoScroll), false);

    private static readonly Brush DefWave = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x8E, 0xEA)));
    private static readonly Brush DefBack = Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)));
    private static readonly Brush DefText = Frozen(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));
    private static readonly Brush DefLine = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
    private static readonly Brush DefPlay = Frozen(new SolidColorBrush(Colors.White));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public static readonly DependencyProperty WaveBrushProperty = Reg<Brush>(nameof(WaveBrush), DefWave);
    public static readonly DependencyProperty HandleBrushProperty = Reg<Brush>(nameof(HandleBrush), DefWave);
    public static readonly DependencyProperty BackgroundBrushProperty = Reg<Brush>(nameof(BackgroundBrush), DefBack);
    public static readonly DependencyProperty TextBrushProperty = Reg<Brush>(nameof(TextBrush), DefText);
    public static readonly DependencyProperty GridBrushProperty = Reg<Brush>(nameof(GridBrush), DefLine);
    public static readonly DependencyProperty PlayheadBrushProperty = Reg<Brush>(nameof(PlayheadBrush), DefPlay);

    public WaveformData? Waveform { get => (WaveformData?)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public bool AutoScroll { get => (bool)GetValue(AutoScrollProperty); set => SetValue(AutoScrollProperty, value); }
    public Brush WaveBrush { get => (Brush)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty, value); }
    public Brush HandleBrush { get => (Brush)GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty, value); }
    public Brush BackgroundBrush { get => (Brush)GetValue(BackgroundBrushProperty); set => SetValue(BackgroundBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush PlayheadBrush { get => (Brush)GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    // ------------------------------------------------------------------ events
    /// <summary>The user dragged a handle or drew a new range: (start, end) in seconds.</summary>
    public event Action<double, double>? SelectionDragged;
    /// <summary>The user clicked a point on the waveform.</summary>
    public event Action<double>? SeekRequested;
    public event EventHandler? ViewChanged;

    // ------------------------------------------------------------------ view window
    private double _viewStart;
    private double _viewSpan = 1;
    public double ViewStart => _viewStart;
    public double ViewSpan => _viewSpan;
    public double Duration => Waveform?.DurationSeconds ?? 0;
    private double MinSpan => Math.Min(0.25, Math.Max(Duration, 0.01));

    private StreamGeometry? _waveGeo;
    private (double Start, double Span, double W, double H) _geoKey;

    private enum Drag { None, Start, End, NewRange, Pan }
    private Drag _drag = Drag.None;
    private double _anchorTime, _downX, _panViewStart;
    private bool _moved;
    private double _hoverX = -1;

    private static readonly Typeface UiFont = new("Segoe UI");

    public WaveformView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height);

    // ------------------------------------------------------------------ coordinate helpers
    private double PlotWidth => Math.Max(1, ActualWidth - 2 * Pad);
    private double TimeToX(double t) => Pad + (t - _viewStart) / _viewSpan * PlotWidth;
    private double XToTime(double x) => _viewStart + (x - Pad) / PlotWidth * _viewSpan;

    private void OnWaveformChanged()
    {
        _waveGeo = null;
        _viewStart = 0;
        _viewSpan = Math.Max(Duration, 0.01);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayheadChanged()
    {
        if (!AutoScroll || Duration <= 0) return;
        double p = Playhead;
        if (p < _viewStart || p > _viewStart + _viewSpan * 0.98)
            SetView(p - _viewSpan * 0.05, _viewSpan);
    }

    // ------------------------------------------------------------------ zoom / scroll API
    public void SetView(double start, double span)
    {
        if (Duration <= 0) return;
        span = Math.Clamp(span, MinSpan, Duration);
        start = Math.Clamp(start, 0, Math.Max(0, Duration - span));
        if (Math.Abs(start - _viewStart) < 1e-9 && Math.Abs(span - _viewSpan) < 1e-9) return;
        _viewStart = start;
        _viewSpan = span;
        _waveGeo = null;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScrollTo(double start) => SetView(start, _viewSpan);

    public void ZoomAround(double time, double factor)
    {
        double newSpan = Math.Clamp(_viewSpan * factor, MinSpan, Duration);
        double ratio = (time - _viewStart) / _viewSpan;
        SetView(time - ratio * newSpan, newSpan);
    }

    public void ZoomIn() => ZoomAround(CenterTime(), 0.6);
    public void ZoomOut() => ZoomAround(CenterTime(), 1 / 0.6);
    public void ZoomAll() => SetView(0, Duration);

    public void ZoomToSelection()
    {
        double len = Math.Max(SelectionEnd - SelectionStart, MinSpan);
        double span = len * 1.25;
        SetView((SelectionStart + SelectionEnd) / 2 - span / 2, span);
    }

    private double CenterTime() =>
        Playhead >= _viewStart && Playhead <= _viewStart + _viewSpan ? Playhead : _viewStart + _viewSpan / 2;

    // ------------------------------------------------------------------ rendering
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        var bounds = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(BackgroundBrush, null, bounds, 6, 6);
        dc.PushClip(new RectangleGeometry(bounds, 6, 6));

        var wf = Waveform;
        if (wf is null || Duration <= 0)
        {
            var ft = Text("No audio loaded", 14, TextBrush);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            dc.Pop();
            return;
        }

        double plotW = PlotWidth;
        double plotL = Pad, plotR = Pad + plotW;
        double bodyTop = RulerHeight, bodyH = h - RulerHeight;
        double mid = bodyTop + bodyH / 2;
        var gridPen = new Pen(GridBrush, 1);

        DrawRuler(dc, w, gridPen);
        dc.DrawLine(gridPen, new Point(plotL, mid), new Point(plotR, mid));

        double xs = TimeToX(SelectionStart), xe = TimeToX(SelectionEnd);
        double cs = Math.Clamp(xs, plotL, plotR), ce = Math.Clamp(xe, plotL, plotR);

        // soft highlight behind the kept range
        if (ce > cs)
        {
            dc.PushOpacity(0.14);
            dc.DrawRectangle(HandleBrush, null, new Rect(cs, bodyTop, ce - cs, bodyH));
            dc.Pop();
        }

        var geo = GetWaveGeometry(wf, plotW, bodyTop, bodyH);
        if (geo is not null)
        {
            dc.PushTransform(new TranslateTransform(Pad, 0));
            dc.DrawGeometry(WaveBrush, null, geo);
            dc.Pop();
        }

        // fade out everything that will be cut away
        dc.PushOpacity(0.62);
        if (cs > plotL) dc.DrawRectangle(BackgroundBrush, null, new Rect(plotL, bodyTop, cs - plotL, bodyH));
        if (ce < plotR) dc.DrawRectangle(BackgroundBrush, null, new Rect(ce, bodyTop, plotR - ce, bodyH));
        dc.Pop();

        DrawHandle(dc, xs, bodyTop, bodyH, mid, w);
        DrawHandle(dc, xe, bodyTop, bodyH, mid, w);

        double xp = TimeToX(Playhead);
        if (xp >= plotL - 1 && xp <= plotR + 1)
            dc.DrawLine(new Pen(PlayheadBrush, 1.5), new Point(xp, RulerHeight - 4), new Point(xp, h));

        if (_hoverX >= plotL && _hoverX <= plotR && _drag == Drag.None)
        {
            dc.PushOpacity(0.55);
            dc.DrawLine(new Pen(TextBrush, 1), new Point(_hoverX, RulerHeight), new Point(_hoverX, h));
            dc.Pop();
            var label = Text(TimeFormat.Format(XToTime(_hoverX)), 11, TextBrush);
            double lx = Math.Min(_hoverX + 6, w - label.Width - 4);
            dc.DrawText(label, new Point(Math.Max(2, lx), h - label.Height - 4));
        }

        dc.Pop();
    }

    private void DrawHandle(DrawingContext dc, double x, double top, double height, double mid, double w)
    {
        if (x < -10 || x > w + 10) return;
        dc.DrawLine(new Pen(HandleBrush, 2), new Point(x, top), new Point(x, top + height));
        var grip = new Rect(x - 4, mid - 15, 8, 30);
        dc.DrawRoundedRectangle(HandleBrush, new Pen(BackgroundBrush, 1.5), grip, 3, 3);
    }

    private void DrawRuler(DrawingContext dc, double w, Pen gridPen)
    {
        double pxPerSec = PlotWidth / _viewSpan;
        double[] steps = { 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };
        double step = steps[^1];
        foreach (var s in steps) if (s * pxPerSec >= 90) { step = s; break; }

        double first = Math.Ceiling(_viewStart / step) * step;
        for (double t = first; t <= _viewStart + _viewSpan + 1e-9; t += step)
        {
            double x = TimeToX(t);
            dc.DrawLine(gridPen, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
            string text = step >= 1 ? TimeFormat.FormatShort(t) : TimeFormat.Format(t)[..^2];
            dc.DrawText(Text(text, 11, TextBrush), new Point(x + 4, 3));
        }
        dc.DrawLine(gridPen, new Point(0, RulerHeight), new Point(w, RulerHeight));
    }

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiFont, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Waveform outline in plot-local coordinates (x = 0 is the left edge of the plot).</summary>
    private StreamGeometry? GetWaveGeometry(WaveformData wf, double plotW, double top, double height)
    {
        var key = (_viewStart, _viewSpan, plotW, height);
        if (_waveGeo is not null && key == _geoKey) return _waveGeo;

        int cols = (int)Math.Ceiling(plotW);
        if (cols < 2) return null;
        double mid = top + height / 2;
        double amp = height / 2 - 3;
        double gain = wf.DisplayGain;
        double bps = wf.BucketsPerSecond;
        int n = wf.BucketCount;

        var tops = new List<Point>(cols);
        var bots = new List<Point>(cols);
        for (int x = 0; x < cols; x++)
        {
            double t0 = _viewStart + x * _viewSpan / plotW;
            double t1 = _viewStart + (x + 1) * _viewSpan / plotW;
            int i0 = (int)Math.Floor(t0 * bps);
            int i1 = Math.Max(i0, (int)Math.Ceiling(t1 * bps) - 1);
            if (i0 >= n) break;
            i0 = Math.Max(0, i0);
            i1 = Math.Min(n - 1, i1);
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = i0; i <= i1; i++)
            {
                if (wf.Min[i] < mn) mn = wf.Min[i];
                if (wf.Max[i] > mx) mx = wf.Max[i];
            }
            double yTop = mid - mx * gain * amp;
            double yBot = mid - mn * gain * amp;
            if (yBot - yTop < 1) { yTop -= 0.5; yBot += 0.5; }
            tops.Add(new Point(x, yTop));
            bots.Add(new Point(x, yBot));
        }
        if (tops.Count < 2) return null;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(tops[0], true, true);
            ctx.PolyLineTo(tops.Skip(1).ToList(), true, false);
            bots.Reverse();
            ctx.PolyLineTo(bots, true, false);
        }
        geo.Freeze();
        _waveGeo = geo;
        _geoKey = key;
        return geo;
    }

    // ------------------------------------------------------------------ pointer logic (testable)
    private bool NearStart(double x) => Math.Abs(x - TimeToX(SelectionStart)) <= GrabRadius;
    private bool NearEnd(double x) => Math.Abs(x - TimeToX(SelectionEnd)) <= GrabRadius;

    /// <summary>Left button: grab a handle, or start drawing a new range (a click without movement seeks).</summary>
    public void PointerDown(double x)
    {
        if (Waveform is null) return;
        _downX = x;
        _moved = false;
        bool ns = NearStart(x), ne = NearEnd(x);
        if (ns && ne) // handles overlap (tiny selection): pick by which side of the middle we're on
            _drag = x < (TimeToX(SelectionStart) + TimeToX(SelectionEnd)) / 2 ? Drag.Start : Drag.End;
        else if (ns) _drag = Drag.Start;
        else if (ne) _drag = Drag.End;
        else
        {
            _drag = Drag.NewRange;
            _anchorTime = Math.Clamp(XToTime(x), 0, Duration);
        }
    }

    /// <summary>Middle/right button: drag to pan.</summary>
    public void PanDown(double x)
    {
        if (Waveform is null) return;
        _drag = Drag.Pan;
        _downX = x;
        _panViewStart = _viewStart;
    }

    public void PointerMove(double x)
    {
        _hoverX = x;
        switch (_drag)
        {
            case Drag.Start:
                SelectionDragged?.Invoke(Math.Clamp(XToTime(x), 0, SelectionEnd - MinSelection), SelectionEnd);
                EdgeScroll(x);
                break;
            case Drag.End:
                SelectionDragged?.Invoke(SelectionStart, Math.Clamp(XToTime(x), SelectionStart + MinSelection, Duration));
                EdgeScroll(x);
                break;
            case Drag.NewRange:
                if (!_moved && Math.Abs(x - _downX) < 4) break;
                _moved = true;
                double b = Math.Clamp(XToTime(x), 0, Duration);
                double lo = Math.Min(_anchorTime, b), hi = Math.Max(_anchorTime, b);
                if (hi - lo < MinSelection) { if (lo + MinSelection <= Duration) hi = lo + MinSelection; else lo = hi - MinSelection; }
                SelectionDragged?.Invoke(lo, hi);
                EdgeScroll(x);
                break;
            case Drag.Pan:
                SetView(_panViewStart - (x - _downX) / PlotWidth * _viewSpan, _viewSpan);
                break;
        }
        InvalidateVisual();
    }

    public void PointerUp(double x)
    {
        if (_drag == Drag.NewRange && !_moved)
            SeekRequested?.Invoke(Math.Clamp(XToTime(x), 0, Duration));
        _drag = Drag.None;
        InvalidateVisual();
    }

    public void Wheel(double x, int delta, bool shift)
    {
        if (Waveform is null) return;
        if (shift) ScrollTo(_viewStart - Math.Sign(delta) * _viewSpan * 0.15);
        else ZoomAround(XToTime(x), Math.Pow(0.8, delta / 120.0));
    }

    private void EdgeScroll(double x)
    {
        if (x < Pad) ScrollTo(_viewStart - _viewSpan * 0.03);
        else if (x > ActualWidth - Pad) ScrollTo(_viewStart + _viewSpan * 0.03);
    }

    /// <summary>Test/diagnostic helper: which handle (if any) would be grabbed at this x.</summary>
    public string HitTest(double x) => NearStart(x) && NearEnd(x) ? "both" : NearStart(x) ? "start" : NearEnd(x) ? "end" : "none";
    public double TimeAtX(double x) => XToTime(x);
    public double XAtTime(double t) => TimeToX(t);

    // ------------------------------------------------------------------ WPF mouse plumbing
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Waveform is null) return;
        Focus();
        PointerDown(e.GetPosition(this).X);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Right) return;
        if (Waveform is null) return;
        PanDown(e.GetPosition(this).X);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        PointerMove(x);
        if (_drag == Drag.None) Cursor = NearStart(x) || NearEnd(x) ? Cursors.SizeWE : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        PointerUp(e.GetPosition(this).X);
        EndCapture();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.Pan && (e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Right))
        {
            PointerUp(e.GetPosition(this).X);
            EndCapture();
        }
    }

    private void EndCapture()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = -1;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Wheel(e.GetPosition(this).X, e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }
}
