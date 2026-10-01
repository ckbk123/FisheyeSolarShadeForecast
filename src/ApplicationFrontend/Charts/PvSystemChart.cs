using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SolarShade.PvBattery;

namespace SolarShade.Desktop;

/// <summary>Actual interval energy and end-of-interval SoC on independent labelled axes; navigation is display-only.</summary>
public sealed class PvSystemChart : FrameworkElement
{
    private BatterySimulationResult? result;
    private double from, to;
    private Point? drag;
    private double dragFrom, dragTo;
    private Point? pointer;
    public bool ShowLoad { get; set; } = true;
    public bool ShowPv { get; set; } = true;
    public bool ShowSoc { get; set; } = true;
    public bool Stale { get; set; }
    public event Action<string>? Hover;
    public static readonly Brush LoadBrush = Brushes.DarkOrange, PvBrush = Brushes.Teal, SocBrush = Brushes.RoyalBlue;
    private static double Time(DateTimeOffset value) => (value - DateTimeOffset.UnixEpoch).TotalSeconds;
    private Rect Plot => new(58, 28, Math.Max(1, ActualWidth - 115), Math.Max(1, ActualHeight - 85));
    public PvSystemChart()
    {
        MinHeight = 140; Focusable = true; ClipToBounds = true;
        System.Windows.Automation.AutomationProperties.SetName(this, "Hourly energy and battery charge. Details are shown beneath the graph and available in exports.");
        MouseWheel += (_, e) =>
        {
            if (result == null) return; var p = Plot;
            double anchor = from + Math.Clamp((e.GetPosition(this).X - p.Left) / p.Width, 0, 1) * (to - from);
            double factor = e.Delta > 0 ? .7 : 1 / .7;
            SetRange(anchor + (from - anchor) * factor, anchor + (to - anchor) * factor); e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) => { if (result == null) return; Focus(); drag = e.GetPosition(this); dragFrom = from; dragTo = to; CaptureMouse(); };
        MouseLeftButtonUp += (_, _) => { drag = null; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => drag = null;
        MouseMove += (_, e) =>
        {
            pointer = e.GetPosition(this);
            if (drag is { } start) { double shift = (pointer.Value.X - start.X) / Plot.Width * (dragTo - dragFrom); SetRange(dragFrom - shift, dragTo - shift); }
            Report(pointer.Value); InvalidateVisual();
        };
        MouseLeave += (_, _) => { pointer = null; InvalidateVisual(); };
    }
    public void SetResult(BatterySimulationResult? value)
    { if (ReferenceEquals(result, value)) return; result = value; Reset(); }
    public void Reset()
    { if (result?.Hours.Count > 0) { from = Time(result.Hours[0].Start); to = Time(result.Hours[^1].End); } InvalidateVisual(); }
    public void Day(DateTime day)
    {
        if (result == null) return;
        var zone = BatterySimulator.ResolveTimeZone(result.Input.Irradiance.TimeZoneId);
        var a = SolarShade.Irradiance.IrradianceDatasets.Boundary(day.Date, zone);
        var b = SolarShade.Irradiance.IrradianceDatasets.Boundary(day.Date.AddDays(1), zone);
        SetRange(Time(a), Time(b));
    }
    private void SetRange(double a, double b)
    {
        if (result == null || b <= a) return;
        double first = Time(result.Hours[0].Start), last = Time(result.Hours[^1].End);
        double span = Math.Clamp(b - a, Math.Min(3600, last - first), last - first);
        from = Math.Clamp(a, first, last - span); to = from + span; InvalidateVisual();
    }
    public static string Describe(BatteryHour h) => $"{h.Start:yyyy-MM-dd HH:mm zzz} → {h.End:HH:mm zzz} · {(h.End - h.Start).TotalMinutes:0.##} min{(h.IsPartialHour ? " (partial hour)" : "")}\nLoad {h.LoadWh:0.##} Wh · PV available {h.PvWh:0.##} Wh · end SoC {h.EndSocPercent:0.##}% · unmet {h.UnmetLoadWh:0.##} Wh";
    private void Report(Point point)
    {
        if (result == null || !Plot.Contains(point)) return;
        double time = from + (point.X - Plot.Left) / Plot.Width * (to - from);
        var rows = result.Hours; int lo = 0, hi = rows.Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (Time(rows[mid].End) <= time) lo = mid + 1; else hi = mid; }
        Hover?.Invoke(Describe(rows[lo]));
    }
    private DrawingGroup? cachedDrawing;
    private (BatterySimulationResult? Result, double From, double To, bool Load, bool Pv, bool Soc, bool Stale, Size Size, double Dpi) drawingKey;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var key = (result, from, to, ShowLoad, ShowPv, ShowSoc, Stale, RenderSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (cachedDrawing == null || drawingKey != key)
        {
            var drawing = new DrawingGroup(); using (var context = drawing.Open()) Draw(context);
            drawing.Freeze(); cachedDrawing = drawing; drawingKey = key;
        }
        dc.DrawDrawing(cachedDrawing);
        if (result != null && (ShowLoad || ShowPv || ShowSoc) && pointer is { } point && Plot.Contains(point))
            dc.DrawLine(new Pen(Brushes.Gray, .8), new(point.X, Plot.Top), new(point.X, Plot.Bottom));
    }
    private void Draw(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (result == null) { Text(dc, "Evaluate your system to display hourly results.", 20, 45); return; }
        if (!ShowLoad && !ShowPv && !ShowSoc) { Text(dc, "Select a series above to show the graph.", 20, 45); return; }
        var plot = Plot; var visible = result.Hours.Where(h => Time(h.End) >= from && Time(h.Start) <= to).ToArray();
        if (visible.Length == 0) return;
        double energyMax = Math.Max(1, visible.Max(h => Math.Max(h.PvWh, h.LoadWh))) * 1.08;
        double X(double time) => plot.Left + (time - from) / (to - from) * plot.Width;
        double Energy(double value) => plot.Bottom - value / energyMax * plot.Height;
        double Soc(double value) => plot.Bottom - value / 100 * plot.Height;
        Text(dc, "Energy · Wh", 5, 3); Text(dc, "SoC · %", ActualWidth - 58, 3);
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - i / 4d * plot.Height;
            dc.DrawLine(new Pen(Brushes.LightGray, .6), new(plot.Left, y), new(plot.Right, y));
            Text(dc, (energyMax * i / 4).ToString("0.#", CultureInfo.InvariantCulture), 3, y - 7);
            Text(dc, (i * 25).ToString(), plot.Right + 7, y - 7);
        }
        var zone = BatterySimulator.ResolveTimeZone(result.Input.Irradiance.TimeZoneId);
        int ticks = Math.Max(2, Math.Min(6, (int)(plot.Width / 120)));
        for (int i = 0; i <= ticks; i++)
        {
            double time = from + (to - from) * i / ticks;
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddSeconds(time), zone);
            double x = X(time);
            Text(dc, local.ToString("dd MMM\nHH:mm zzz", CultureInfo.InvariantCulture), Math.Clamp(x - 33, 0, Math.Max(0, ActualWidth - 76)), plot.Bottom + 8);
        }
        dc.PushClip(new RectangleGeometry(plot));
        var loadPen = new Pen(LoadBrush, 1.4) { DashStyle = DashStyles.Dash };
        var pvPen = new Pen(PvBrush, 1.5); var socPen = new Pen(SocBrush, 1.8);
        if (visible.Length <= plot.Width * 3)
        {
            Point? previousSoc = null;
            BatteryHour? previousHour = null;
            foreach (var h in visible)
            {
                double a = X(Time(h.Start)), b = X(Time(h.End));
                if (ShowLoad) dc.DrawLine(loadPen, new(a, Energy(h.LoadWh)), new(b, Energy(h.LoadWh)));
                if (ShowPv) dc.DrawLine(pvPen, new(a, Energy(h.PvWh)), new(b, Energy(h.PvWh)));
                if (previousHour is { } previous && previous.End == h.Start)
                {
                    if (ShowLoad) dc.DrawLine(loadPen, new(a, Energy(previous.LoadWh)), new(a, Energy(h.LoadWh)));
                    if (ShowPv) dc.DrawLine(pvPen, new(a, Energy(previous.PvWh)), new(a, Energy(h.PvWh)));
                }
                if (ShowSoc)
                {
                    var start = previousSoc ?? new Point(a, Soc(h.StartStoredWh / result.Input.Settings.BatteryCapacityWh * 100));
                    var end = new Point(b, Soc(h.EndSocPercent)); dc.DrawLine(socPen, start, end); previousSoc = end;
                }
                if (h.UnmetLoadWh > 0) dc.DrawRectangle(Brushes.Crimson, null, new Rect(a, plot.Bottom - 4, Math.Max(1, b - a), 4));
                previousHour = h;
            }
        }
        else
        {
            // Pixel envelopes retain extrema and every shortfall; never average away a depletion or generation peak.
            foreach (var group in visible.GroupBy(h => (int)X(Time(h.End))))
            {
                double x = group.Key;
                if (ShowLoad) dc.DrawLine(loadPen, new(x, Energy(group.Min(h => h.LoadWh))), new(x, Energy(group.Max(h => h.LoadWh)) - .5));
                if (ShowPv) dc.DrawLine(pvPen, new(x, Energy(group.Min(h => h.PvWh))), new(x, Energy(group.Max(h => h.PvWh)) - .5));
                if (ShowSoc) dc.DrawLine(socPen, new(x, Soc(group.Min(h => Math.Min(h.EndSocPercent, h.StartStoredWh / result.Input.Settings.BatteryCapacityWh * 100)))) , new(x, Soc(group.Max(h => Math.Max(h.EndSocPercent, h.StartStoredWh / result.Input.Settings.BatteryCapacityWh * 100))) - .5));
                if (group.Any(h => h.UnmetLoadWh > 0)) dc.DrawRectangle(Brushes.Crimson, null, new Rect(x, plot.Bottom - 4, 1, 4));
            }
        }
        if (ShowSoc && Time(result.Hours[0].Start) >= from) dc.DrawEllipse(SocBrush, null, new(X(Time(result.Hours[0].Start)), Soc(result.Input.Settings.InitialSoc * 100)), 3, 3);
        dc.Pop();
        if (Stale) Text(dc, "PREVIOUS RESULTS · evaluate again", plot.Left + 10, plot.Top + 10, Brushes.Crimson, 15);
    }
    private void Text(DrawingContext dc, string value, double x, double y, Brush? brush = null, double size = 10) =>
        dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush ?? Brushes.DarkSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
}
