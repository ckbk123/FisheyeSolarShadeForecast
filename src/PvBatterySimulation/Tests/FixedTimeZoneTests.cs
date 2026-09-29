using SolarShade.PvBattery;
using Xunit;

public sealed class FixedTimeZoneTests
{
    [Theory]
    [InlineData("Fixed/UTC+05:45", 345)]
    [InlineData("Fixed/UTC-03:30", -210)]
    [InlineData("Fixed/UTC+14:00", 840)]
    [InlineData("Fixed/UTC+00:00", 0)]
    public void FixedOffsetPreservesLocalHourlyLoad(string zoneId, int minutes)
    {
        var zone = BatterySimulator.ResolveTimeZone(zoneId); Assert.Equal(TimeSpan.FromMinutes(minutes), zone.BaseUtcOffset);
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, zone.BaseUtcOffset);
        var settings = new BatterySimulationSettings(0, .2, .9, Enumerable.Range(0, 24).Select(i => (double)i).ToArray(), 1000, 1);
        var result = BatterySimulator.Compute(new(settings, new(new[] { new ShadedIrradianceInterval("a", start, start.AddDays(1), 0) }, zoneId)));
        Assert.Equal(24, result.Hours.Count); Assert.Equal(276, result.Summary.LoadWh);
        Assert.Equal(start.Offset, result.Hours[0].Start.Offset); Assert.Equal(0, result.Hours[0].LoadWh); Assert.Equal(23, result.Hours[^1].LoadWh);
    }
    [Theory]
    [InlineData("Fixed/UTC+14:01")]
    [InlineData("Fixed/UTC-03:60")]
    public void InvalidFixedOffsetsAreRejected(string id) => Assert.Throws<ArgumentException>(() => BatterySimulator.ResolveTimeZone(id));
}
