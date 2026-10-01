using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SolarShade.Desktop;

public sealed class IrradianceChart : FrameworkElement
{
    private const double Left = 58, RightMargin = 24, Top = 26, BottomMargin = 66;
    private PanelRow[] rows = [];
    private TimeZoneInfo zone = TimeZoneInfo.Local;
    private int first, last;
    private Point? drag;
    public int Component { get; set; }
    public Action<string>? HoverText { get; set; }
    private static readonly Pen Amber = Stroke(216, 146, 38, 1.5), Teal = Stroke(17, 137, 122, 1.5);
    private static readonly Pen MajorGrid = Stroke(221, 229, 233), MinorGrid = Stroke(240, 243, 245);
    private static readonly Pen DayGrid = Stroke(171, 189, 198), HourGrid = Stroke(230, 237, 240);
    private static readonly Pen Axis = Stroke(128, 148, 158);
    private static Pen Stroke(byte r, byte g, byte b, double width = 1)
    { var pen = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), width); pen.Freeze(); return pen; }
    private double PlotWidth => Math.Max(1, ActualWidth - Left - RightMargin);
    private double Fraction(double x) => Math.Clamp((x - Left) / PlotWidth, 0, 1);
    private DateTimeOffset TimeAt(double x) => rows[first].Start + (rows[last].End - rows[first].Start) * Fraction(x);

    public IrradianceChart()
    {
        MinHeight = 180; ClipToBounds = true; Cursor = Cursors.Cross;
        MouseWheel += (_, e) =>
        {
            if (rows.Length < 3) return;
            double anchor = Fraction(e.GetPosition(this).X);
            int span = Math.Max(2, (int)((last - first) * (e.Delta > 0 ? .7 : 1.4)));
            span = Math.Min(span, rows.Length - 1); int center = IndexAt(TimeAt(e.GetPosition(this).X));
            first = Math.Clamp(center - (int)(span * anchor), 0, rows.Length - 1 - span); last = first + span;
            InvalidateVisual(); e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) => { drag = e.GetPosition(this); CaptureMouse(); };
        MouseLeftButtonUp += (_, _) => { drag = null; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => drag = null;
        MouseMove += (_, e) =>
        {
            if (rows.Length == 0) return;
            var p = e.GetPosition(this);
            if (drag is { } old && e.LeftButton == MouseButtonState.Pressed)
            {
                int step = (int)((old.X - p.X) * (last - first + 1) / PlotWidth);
                if (step != 0)
                { int span = last - first; first = Math.Clamp(first + step, 0, rows.Length - 1 - span); last = first + span; drag = p; InvalidateVisual(); }
            }
            var time = TimeAt(p.X); var r = rows[IndexAt(time)];
            if (time < r.Start || time > r.End) { HoverText?.Invoke("No data for this interval"); return; }
            var a = TimeZoneInfo.ConvertTime(r.Start, zone); var b = TimeZoneInfo.ConvertTime(r.End, zone);
            string end = b.ToString(a.Date != b.Date ? "dd.MM.yyyy HH:mm zzz" : "HH:mm zzz", CultureInfo.InvariantCulture);
            HoverText?.Invoke($"{a:dd.MM.yyyy HH:mm zzz} – {end}   |   Before {Before(r):F1} W/m²   |   After {(After(r) is { } v ? v.ToString("F1") : "—")} W/m²");
        };
    }
    private int IndexAt(DateTimeOffset time)
    {
        int lo = first, hi = last;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (rows[mid].Start <= time) lo = mid; else hi = mid - 1; }
        return lo;
    }
    public void SetData(IReadOnlyList<PanelRow> values, TimeZoneInfo timeZone)
    {
        bool same = rows.Length == values.Count && rows.Length > 0 && rows[0].Start == values[0].Start && rows[^1].End == values[^1].End;
        rows = values.ToArray(); zone = timeZone; if (!same) Reset(); else InvalidateVisual();
    }
    public void Reset() { first = 0; last = Math.Max(0, rows.Length - 1); InvalidateVisual(); }
    public void Day(DateTime day)
    {
        int a = Array.FindIndex(rows, r => TimeZoneInfo.ConvertTime(r.Start, zone).Date == day.Date);
        int b = Array.FindLastIndex(rows, r => TimeZoneInfo.ConvertTime(r.Start, zone).Date == day.Date);
        if (a >= 0) { first = a; last = b; InvalidateVisual(); }
    }
    private double Before(PanelRow r) => Component switch { 1 => r.BeforeDirect, 2 => r.BeforeDiffuse, _ => r.BeforeTotal };
    private double? After(PanelRow r) => Component switch { 1 => r.AfterDirect, 2 => r.AfterDiffuse, _ => r.AfterTotal };
    private DrawingGroup? cachedDrawing;
    private (PanelRow[] Rows, int First, int Last, int Component, Size Size, double Dpi) drawingKey;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var key = (rows, first, last, Component, RenderSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (cachedDrawing == null || drawingKey != key)
        {
            var drawing = new DrawingGroup(); using (var context = drawing.Open()) Draw(context);
            drawing.Freeze(); cachedDrawing = drawing; drawingKey = key;
        }
        dc.DrawDrawing(cachedDrawing);
    }
    private void Draw(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        if (w < 100 || h < 100) return;
        double right = w - RightMargin, bottom = h - BottomMargin;
        FormattedText Label(string value, double size = 11, Brush? brush = null) => new(value, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush ?? Brushes.SlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        void Text(string value, double x, double y, double size = 11) => dc.DrawText(Label(value, size), new(x, y));
        if (rows.Length == 0)
        { Text("Your panel irradiance will appear here", Left + 30, h / 2 - 16, 18); Text("Choose your inputs, then select Update results.", Left + 30, h / 2 + 14); return; }

        var start = rows[first].Start; var end = rows[last].End;
        if (end <= start) return;
        double max = 100;
        for (int i = first; i <= last; i++) max = Math.Max(max, Math.Max(Before(rows[i]), After(rows[i]) ?? 0));
        max = Math.Ceiling(max / 100) * 100;
        double X(DateTimeOffset time) => Left + PlotWidth * (time - start).TotalSeconds / (end - start).TotalSeconds;
        double Y(double value) => bottom - (bottom - Top) * value / max;
        double previousLabelTop = double.PositiveInfinity;
        for (double value = 0; value <= max; value += 50)
        {
            double y = Y(value); bool major = value % 100 == 0;
            dc.DrawLine(major ? MajorGrid : MinorGrid, new(Left, y), new(right, y));
            dc.DrawLine(Axis, new(Left - (major ? 6 : 3), y), new(Left, y));
            if (major)
            {
                var label = Label(value.ToString("0"), 10);
                // Retain every tick/grid at small window heights; thin labels only to avoid overlap.
                if (y + label.Height / 2 + 1 <= previousLabelTop)
                { dc.DrawText(label, new(Left - 10 - label.Width, y - label.Height / 2)); previousLabelTop = y - label.Height / 2; }
            }
        }
        Text("W/m²", 8, 3);
        var timeAxis = ChartTimeAxis.Create(start, end, zone);
        var localStart = TimeZoneInfo.ConvertTime(start, zone); var localEnd = TimeZoneInfo.ConvertTime(end.AddTicks(-1), zone);
        var caption = Label($"{localStart:dd.MM.yyyy} – {localEnd:dd.MM.yyyy}  ·  {TimeZoneSelection.Describe(zone, start, end)}", 10);
        caption.MaxTextWidth = Math.Max(1, PlotWidth); caption.MaxLineCount = 1; caption.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(caption, new(Left, 3));

        double dayWidth = PlotWidth / Math.Max(1, (end - start).TotalDays);
        foreach (var tick in timeAxis.Ticks)
        {
            double x = X(tick.Time); bool midnight = tick.Hour == 0;
            dc.DrawLine(midnight ? DayGrid : HourGrid, new(x, Top), new(x, bottom));
            dc.DrawLine(midnight ? Axis : DayGrid, new(x, bottom), new(x, bottom + 5));
            if (midnight && dayWidth >= 40) dc.DrawLine(Axis, new(x, bottom + 26), new(x, bottom + 47));
        }
        // Bound dense display geometry to pixel columns while retaining peaks, troughs and gaps.
        // Full values remain authoritative for hover, calculations and exports.
        dc.PushClip(new RectangleGeometry(new Rect(Left, Top, PlotWidth, bottom - Top)));
        void Draw(Func<PanelRow, double?> selector, Pen pen)
        {
            if (last - first > PlotWidth * 2)
            {
                // Filled one-pixel envelopes avoid expensive tessellation of thousands of
                // near-vertical, overlapping antialiased strokes on the software renderer.
                foreach (var band in ChartSteps.Envelopes(rows, first, last, selector, (int)PlotWidth))
                    dc.DrawRectangle(pen.Brush, null, new Rect(Left + band.Column, Y(band.Maximum), Math.Max(1, X(band.End) - Left - band.Column), Math.Max(1, Y(band.Minimum) - Y(band.Maximum))));
                return;
            }
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
                foreach (var vertex in ChartSteps.Vertices(rows, first, last, selector))
                {
                    var point = new Point(X(vertex.Time), Y(vertex.Value));
                    if (vertex.StartFigure) context.BeginFigure(point, false, false);
                    else context.LineTo(point, true, false);
                }
            geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
        }
        Draw(r => Before(r), Amber); Draw(After, Teal);
        dc.Pop();
        dc.DrawLine(Axis, new(Left, Top), new(Left, bottom));
        dc.DrawLine(Axis, new(Left, bottom), new(right, bottom));
        dc.DrawLine(MajorGrid, new(Left, bottom + 26), new(right, bottom + 26));

        int labelHours = Math.Max(3, timeAxis.GridHours);
        double hourLabelWidth = Label("21h", 10).Width + 5;
        while (labelHours < 24 && PlotWidth * labelHours / (end - start).TotalHours < hourLabelWidth) labelHours *= 2;
        var hourLabels = new Dictionary<int, FormattedText>();
        if (timeAxis.GridHours < 24)
            foreach (var tick in timeAxis.Ticks.Where(t => t.Hour % labelHours == 0))
            {
                if (!hourLabels.TryGetValue(tick.Hour, out var label)) hourLabels[tick.Hour] = label = Label($"{tick.Hour}h", 10);
                dc.DrawText(label, new(X(tick.Time) - label.Width / 2, bottom + 7));
            }
        double previousRight = double.NegativeInfinity;
        double shortDateWidth = Label("00.00", 11).Width, fullDateWidth = Label("00.00.0000", 11).Width;
        foreach (var day in timeAxis.Days)
        {
            double a = X(day.Start), b = X(day.End), center = (a + b) / 2;
            double width = b - a >= 82 ? fullDateWidth : shortDateWidth;
            if (center - width / 2 < Left || center + width / 2 > right || center - width / 2 < previousRight + 8) continue;
            var label = Label(day.Date.ToString(b - a >= 82 ? "dd.MM.yyyy" : "dd.MM"), 11);
            // On long views a date spans several narrow day bands; keep separators out of its text.
            dc.DrawRectangle(Brushes.White, null, new Rect(center - label.Width / 2 - 2, bottom + 29, label.Width + 4, label.Height + 3));
            dc.DrawText(label, new(center - label.Width / 2, bottom + 31)); previousRight = center + label.Width / 2;
        }
    }
}
