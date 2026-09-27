using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.Desktop;
using Xunit;

public class ChartTests
{
    private static readonly DateTimeOffset Start = new(2023, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static PanelRow Row(DateTimeOffset start, double value, double? after = 0, double hours = 1) =>
        new(start.AddHours(hours), start, start.AddHours(hours), value, 0, after, after.HasValue ? 0 : null, null);

    [Theory]
    [InlineData(1, 1)][InlineData(2, 3)][InlineData(7, 3)][InlineData(8, 6)]
    [InlineData(27, 6)][InlineData(28, 12)][InlineData(30, 12)][InlineData(31, 12)][InlineData(32, 24)]
    public void ZoomGridFollowsCalendarSpan(int days, int gridHours)
    {
        var axis = ChartTimeAxis.Create(Start, Start.AddDays(days), TimeZoneInfo.Utc);
        Assert.Equal(gridHours, axis.GridHours);
        Assert.Equal(days, axis.Days.Length);
        Assert.Equal(Start, axis.Ticks[0].Time);
        Assert.Equal(Start.AddDays(days), axis.Ticks[^1].Time);
        Assert.Equal(12, (axis.Days[0].Start + (axis.Days[0].End - axis.Days[0].Start) / 2).Hour);
    }

    [Fact]
    public void TicksAlignToSelectedZoneRatherThanUtcOrFirstSample()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var start = new DateTimeOffset(2023, 3, 1, 1, 15, 0, TimeSpan.FromHours(5.5));
        var axis = ChartTimeAxis.Create(start, start.AddDays(2), zone);
        Assert.Equal(new DateTimeOffset(2023, 3, 1, 3, 0, 0, TimeSpan.FromHours(5.5)), axis.Ticks[0].Time);
        Assert.All(axis.Ticks, t => Assert.Equal(0, TimeZoneInfo.ConvertTime(t.Time, zone).Hour % 3));
        Assert.Equal(start, axis.Days[0].Start);
        Assert.Equal(start.AddDays(2), axis.Days[^1].End);
    }

    [Theory][InlineData(2025, 3, 9, 23)][InlineData(2025, 11, 2, 25)]
    public void DaylightSavingDaysKeepHourlyGridAndActualDurations(int year, int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var date = new DateTime(year, month, day);
        var start = SolarShade.Irradiance.IrradianceDatasets.Boundary(date, zone); var end = SolarShade.Irradiance.IrradianceDatasets.Boundary(date.AddDays(1), zone);
        var axis = ChartTimeAxis.Create(start, end, zone);
        Assert.Equal(1, axis.GridHours);
        Assert.Equal(hours + 1, axis.Ticks.Length);
        Assert.Equal(hours, Assert.Single(axis.Days).End.Subtract(start).TotalHours);
        Assert.Equal(axis.Ticks.Length, axis.Ticks.Select(t => t.Time.UtcTicks).Distinct().Count());
        Assert.All(axis.Ticks.Zip(axis.Ticks.Skip(1)), pair => Assert.Equal(1, (pair.Second.Time - pair.First.Time).TotalHours));
    }

    [Fact]
    public void StaircaseHoldsIntervalMeansIncludingTheFinalHour()
    {
        PanelRow[] rows = [Row(Start, 100), Row(Start.AddHours(1), 700), Row(Start.AddHours(2), 200, hours: .5)];
        var points = ChartSteps.Vertices(rows, 0, 2, r => r.BeforeTotal).ToArray();
        Assert.Equal(Start, points[0].Time); // Source labels are at interval ends in this fixture.
        Assert.Equal(Start.AddHours(2.5), points[^1].Time);
        double energy = 0;
        foreach (var (a, b) in points.Zip(points.Skip(1)))
        {
            Assert.True(a.Time == b.Time || a.Value == b.Value, "A staircase segment must be vertical or horizontal.");
            energy += (b.Time - a.Time).TotalHours * a.Value;
        }
        Assert.Equal(900, energy);
        Assert.Single(points, p => p.StartFigure);
    }

    [Fact]
    public void MissingValuesAndMissingTimeDoNotCreateConnectingLines()
    {
        PanelRow[] rows = [Row(Start, 1, 1), Row(Start.AddHours(1), 2, null),
            Row(Start.AddHours(2), 3, 3), Row(Start.AddHours(5), 4, 4)];
        var points = ChartSteps.Vertices(rows, 0, 3, r => r.AfterTotal).ToArray();
        Assert.Equal(3, points.Count(p => p.StartFigure));
        Assert.DoesNotContain(points, p => p.Time > Start.AddHours(3) && p.Time < Start.AddHours(5));
    }

    [Fact]
    public void RenderZoomLevelsAndRecordPerformance()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                var start = new DateTimeOffset(2023, 3, 1, 0, 0, 0, TimeSpan.FromHours(7));
                var all = Enumerable.Range(0, 24 * 365).Select(i =>
                {
                    double value = Math.Max(0, Math.Sin((i % 24 - 6) / 12d * Math.PI)) * (i % 24 is 10 or 14 ? 360 : 730);
                    return Row(start.AddHours(i), value, value * (i % 24 < 10 ? .45 : .8));
                }).ToArray();
                string? output = Environment.GetEnvironmentVariable("SOLARSHADE_CHART_CAPTURE_DIR");
                if (output != null) Directory.CreateDirectory(output);
                var timings = new List<object>();
                foreach (var (days, width, height) in new[] { (1, 1000, 350), (2, 1000, 350), (7, 1000, 350),
                    (8, 1000, 350), (28, 1000, 350), (31, 1000, 350), (32, 1000, 350), (365, 1000, 350), (7, 600, 180) })
                {
                    var chart = new IrradianceChart { Width = width, Height = height };
                    chart.SetData(all.Take(days * 24).ToArray(), zone);
                    var watch = Stopwatch.StartNew();
                    chart.Measure(new Size(width, height)); chart.Arrange(new Rect(0, 0, width, height)); chart.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(chart);
                    timings.Add(new { days, width, height, milliseconds = watch.Elapsed.TotalMilliseconds });
                    if (output != null)
                    {
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, $"chart-{days}days-{width}.png")); encoder.Save(file);
                    }
                }
                if (output != null) File.WriteAllText(Path.Combine(output, "render-timings.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
