namespace SolarShade.Desktop;

public readonly record struct ChartTimeTick(DateTimeOffset Time, int Hour);
public readonly record struct ChartDay(DateTime Date, DateTimeOffset Start, DateTimeOffset End);
public sealed record ChartTimeAxis(int GridHours, ChartTimeTick[] Ticks, ChartDay[] Days)
{
    public static ChartTimeAxis Create(DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        if (end <= start) return new(1, [], []);
        var localStart = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var localEnd = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        // Use wall-clock days so a daylight-saving day still receives the one-day grid.
        double days = (localEnd - localStart).TotalDays;
        int hours = days <= 1 ? 1 : days <= 7 ? 3 : days < 28 ? 6 : days <= 31 ? 12 : 24;
        var ticks = new List<ChartTimeTick>();
        var bands = new List<ChartDay>();
        for (var day = localStart.Date; day <= localEnd.Date; day = day.AddDays(1))
        {
            var a = DayStart(day, zone); var b = DayStart(day.AddDays(1), zone);
            if (a < end && b > start)
                bands.Add(new(day, a < start ? start : a, b > end ? end : b));
            for (int hour = 0; hour < 24; hour += hours)
                foreach (var time in Instants(day.AddHours(hour), zone))
                    if (time >= start && time <= end) ticks.Add(new(time, hour));
        }
        return new(hours, ticks.OrderBy(t => t.Time).ToArray(), bands.ToArray());
    }

    private static DateTimeOffset DayStart(DateTime day, TimeZoneInfo zone)
    {
        // A few time zones change clocks at midnight. The first valid instant begins that day.
        while (zone.IsInvalidTime(day)) day = day.AddMinutes(1);
        return Instants(day, zone).Min();
    }
    private static IEnumerable<DateTimeOffset> Instants(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) yield break;
        if (zone.IsAmbiguousTime(local))
        {
            foreach (var offset in zone.GetAmbiguousTimeOffsets(local)) yield return new(local, offset);
        }
        else yield return new(local, zone.GetUtcOffset(local));
    }
}

public readonly record struct ChartVertex(DateTimeOffset Time, double Value, bool StartFigure);
public static class ChartSteps
{
    public readonly record struct Envelope(int Column, DateTimeOffset Start, DateTimeOffset End, double Minimum, double Maximum, bool StartFigure);
    public static IEnumerable<Envelope> Envelopes(PanelRow[] rows, int first, int last, Func<PanelRow, double?> value, int pixels)
    {
        if (last < first) yield break;
        long start = rows[first].Start.UtcTicks, duration = Math.Max(1, rows[last].End.UtcTicks - start);
        Envelope? current = null; DateTimeOffset? previousEnd = null;
        for (int i = first; i <= last; i++)
        {
            var row = rows[i]; var selected = value(row);
            if (selected is not { } v || !double.IsFinite(v) || row.End <= row.Start)
            { if (current is { } complete) yield return complete; current = null; previousEnd = null; continue; }
            int column = (int)((row.Start.UtcTicks - start) / (double)duration * Math.Max(1, pixels));
            if (current is not { } bucket || bucket.Column != column || previousEnd != row.Start)
            {
                if (current is { } complete) yield return complete;
                current = new(column, row.Start, row.End, v, v, previousEnd != row.Start);
            }
            else current = bucket with { End = row.End, Minimum = Math.Min(bucket.Minimum, v), Maximum = Math.Max(bucket.Maximum, v) };
            previousEnd = row.End;
        }
        if (current is { } final) yield return final;
    }

    public static IEnumerable<ChartVertex> Vertices(PanelRow[] rows, int first, int last, Func<PanelRow, double?> value)
    {
        DateTimeOffset? previousEnd = null;
        for (int i = first; i <= last; i++)
        {
            var row = rows[i]; var v = value(row);
            if (v is null || !double.IsFinite(v.Value) || row.End <= row.Start)
            { previousEnd = null; continue; }
            // Interval means describe intervals, irrespective of the source's timestamp convention.
            yield return new(row.Start, v.Value, previousEnd != row.Start);
            yield return new(row.End, v.Value, false);
            previousEnd = row.End;
        }
    }
}

