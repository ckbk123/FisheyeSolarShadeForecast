using SolarShade.PvBattery.IO;
using SolarShade.PvBattery.Validator;
using Xunit;
using static SolarShade.PvBattery.Tests.BatterySimulatorTests;

namespace SolarShade.PvBattery.Tests;

public class ReferenceFixtureTests
{
    [Fact]
    public void FrozenTwoDayProfileMatchesHandCalculation()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var result = BatterySimulator.Compute(BatteryFiles.ReadRequest(Path.Combine(fixture, "two-day-request.json")));
        ReferenceChecks.Verify(result);
        ReferenceChecks.VerifyFixture(result, Path.Combine(fixture, "two-day-expected.json"));
        Assert.Equal(48, result.Hours.Count);
        Assert.Equal(70, result.Summary.FinalStoredWh);
        Assert.Equal(0, result.Summary.HoursContainingShortfall);
        Assert.Null(result.Summary.FirstShortfallIntervalStart);
        Assert.Throws<InvalidDataException>(() => ReferenceChecks.VerifyFixture(
            BatterySimulator.Compute(Request(0, 0)), Path.Combine(fixture, "two-day-expected.json")));
    }

    [Fact]
    public void IndependentDecimalCheckHandlesShortfallAndRandomSubhourIntervals()
    {
        var empty = BatterySimulator.Compute(Request(0, 100, initial: .05, hours: 3));
        ReferenceChecks.Verify(empty);
        Assert.Equal(Start, empty.Summary.FirstShortfallIntervalStart);
        Assert.Equal(3, empty.Summary.HoursContainingShortfall);
        var random = new Random(911);
        var rows = Enumerable.Range(0, 600).Select(i => new ShadedIrradianceInterval(i.ToString(),
            Start.AddMinutes(i * 13), Start.AddMinutes((i + 1) * 13), random.NextDouble() * 1100)).ToArray();
        ReferenceChecks.Verify(BatterySimulator.Compute(new(new(1.7, .21, .94,
            Enumerable.Range(0, 24).Select(_ => random.NextDouble() * 310).ToArray(), 1000, .33), new(rows, "Asia/Kathmandu"))));
    }
}
