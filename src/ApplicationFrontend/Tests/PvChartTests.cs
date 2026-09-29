using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.Desktop;
using SolarShade.PvBattery;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class PvChartTests
{
    private static int RedPixels(PvSystemChart chart)
    {
        chart.Measure(new Size(600, 300)); chart.Arrange(new Rect(0, 0, 600, 300)); chart.UpdateLayout();
        var bitmap = new RenderTargetBitmap(600, 300, 96, 96, PixelFormats.Pbgra32); bitmap.Render(chart);
        var pixels = new byte[600 * 300 * 4]; bitmap.CopyPixels(pixels, 600 * 4, 0);
        return Enumerable.Range(0, 600 * 300).Count(i => pixels[i * 4 + 2] > 180 && pixels[i * 4 + 1] < 50 && pixels[i * 4] < 100);
    }
    [Fact]
    public void RenderMarksSubhourShortfallDespitePositiveEndChargeAndNavigationDoesNotResetIt() => Sta(() =>
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = BatterySimulator.Compute(new(new(1, 1, 1, Enumerable.Repeat(100d, 24).ToArray(), 100, 0),
            new(new[] { new ShadedIrradianceInterval("dark", start, start.AddMinutes(30), 0),
                new ShadedIrradianceInterval("sun", start.AddMinutes(30), start.AddHours(1), 300) }, "UTC")));
        Assert.True(result.Hours[0].EndSocPercent > 0); Assert.True(result.Hours[0].UnmetLoadWh > 0);
        var chart = new PvSystemChart(); chart.SetResult(result); Assert.True(RedPixels(chart) > 0);
        chart.Day(start.Date); Assert.True(RedPixels(chart) > 0);
        chart.ShowSoc = false; chart.ShowPv = false; chart.InvalidateVisual(); Assert.True(RedPixels(chart) > 0);
        Assert.Equal(100, result.Hours[0].EndSocPercent); return Task.CompletedTask;
    });
    [Fact]
    public void DenseDisplayRetainsShortfallsWhileZeroChargeWithoutShortfallHasNoRedMark() => Sta(() =>
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var settings = new BatterySimulationSettings(0, 1, 1, Enumerable.Repeat(1d, 24).ToArray(), 1, 0);
        var result = BatterySimulator.Compute(new(settings, new(new[] { new ShadedIrradianceInterval("long", start, start.AddDays(80), 0) }, "UTC")));
        var chart = new PvSystemChart(); chart.SetResult(result); Assert.True(RedPixels(chart) > 0);
        result = BatterySimulator.Compute(new(settings with { HourlyLoadWh = new double[24] }, result.Input.Irradiance));
        chart.SetResult(result); Assert.Equal(0, RedPixels(chart)); Assert.Equal(0, result.Summary.MinimumSocPercent);
        return Task.CompletedTask;
    });
    [Fact]
    public void HourDetailsDistinguishRepeatedLocalHoursAndPartialDurations()
    {
        var start = new DateTimeOffset(2025, 11, 2, 1, 30, 0, TimeSpan.FromHours(-4));
        var end = start.ToOffset(TimeSpan.FromHours(-5)).AddMinutes(30);
        var row = new BatteryHour(start, end, true, 0, 0, 0, 0, 20, 0, 20, 0);
        string text = PvSystemChart.Describe(row);
        Assert.Contains("-04:00", text); Assert.Contains("-05:00", text); Assert.Contains("30 min (partial hour)", text); Assert.Contains("unmet 20", text);
    }
}
