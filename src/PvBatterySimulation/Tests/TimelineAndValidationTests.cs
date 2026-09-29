using SolarShade.PvBattery;
using Xunit;
using static SolarShade.PvBattery.Tests.BatterySimulatorTests;

namespace SolarShade.PvBattery.Tests;

public class TimelineAndValidationTests
{
    [Theory]
    [InlineData("2025-03-09T00:00:00-05:00", "2025-03-10T00:00:00-04:00", 23, 2, 0)]
    [InlineData("2025-11-02T00:00:00-04:00", "2025-11-03T00:00:00-05:00", 25, 1, 2)]
    public void DstUsesActualElapsedHoursAndRepeatsCivilLoad(string start, string end, int count, int changedHour, int occurrences)
    {
        var request = Request(0, 0) with
        {
            Settings = Request(0, 0).Settings with { HourlyLoadWh = Enumerable.Range(0, 24).Select(i => (double)i).ToArray() },
            Irradiance = new([new("dst", DateTimeOffset.Parse(start), DateTimeOffset.Parse(end), 0)], "America/New_York")
        };
        var result = BatterySimulator.Compute(request);
        Assert.Equal(count, result.Hours.Count);
        Assert.Equal(occurrences, result.Hours.Count(h => h.Start.Hour == changedHour));
        Assert.Equal(count == 23 ? 274 : 277, result.Summary.LoadWh);
        Assert.All(result.Hours, h =>
        {
            Assert.Equal(TimeSpan.FromHours(1), h.End - h.Start);
            Assert.False(h.IsPartialHour);
            Assert.Equal(h.Start.Hour, h.LoadWh);
        });
        Assert.Equal(DateTimeOffset.Parse(end), result.Hours[^1].End);
    }

    [Theory]
    [InlineData("2025-10-05T01:00:00+10:30", "2025-10-05T04:00:00+11:00", 3, 2.5)]
    [InlineData("2025-04-06T01:00:00+11:00", "2025-04-06T04:00:00+10:30", 4, 3.5)]
    public void HalfHourDstTransitionPreservesDuration(string start, string end, int count, double hours)
    {
        var request = Request(0, 10) with
        {
            Irradiance = new([new("dst", DateTimeOffset.Parse(start), DateTimeOffset.Parse(end), 0)], "Australia/Lord_Howe")
        };
        var result = BatterySimulator.Compute(request);
        Assert.Equal(count, result.Hours.Count);
        Assert.Equal(hours * 10, result.Summary.LoadWh);
        Assert.Equal(hours, result.Hours.Sum(h => (h.End - h.Start).TotalHours));
        Assert.Contains(result.Hours, h => h.IsPartialHour);
    }

    [Theory]
    [InlineData("Asia/Ho_Chi_Minh", 7, 1)]
    [InlineData("Asia/Kathmandu", 5, 2)]
    public void LoadUsesStudyTimezoneNotSourceOffset(string zone, int firstHour, int count)
    {
        var request = Request(0, 0);
        request = request with
        {
            Settings = request.Settings with { HourlyLoadWh = Enumerable.Range(0, 24).Select(i => (double)i).ToArray() },
            Irradiance = request.Irradiance with { TimeZoneId = zone }
        };
        var result = BatterySimulator.Compute(request);
        Assert.Equal(firstHour, result.Hours[0].Start.Hour);
        Assert.Equal(count, result.Hours.Count);
        Assert.Equal(zone == "Asia/Kathmandu" ? 5.75 : 7, result.Summary.LoadWh);
    }

    [Fact]
    public void LeapYearAndMinuteCadencePreserveAllEnergy()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var input = Request(10, 10) with { Irradiance = new([new("year", start, start.AddYears(1), 10)], "UTC") };
        var result = BatterySimulator.Compute(input);
        Assert.Equal(8784, result.Hours.Count);
        Assert.Equal(87840, result.Summary.PvWh);
        Assert.Equal(500, result.Summary.FinalStoredWh);
        input = Request(60, 30) with { Irradiance = new(Enumerable.Range(0, 60).Select(i =>
            new ShadedIrradianceInterval(i.ToString(), Start.AddMinutes(i), Start.AddMinutes(i + 1), 60)).ToArray(), "UTC") };
        result = BatterySimulator.Compute(input);
        Assert.Single(result.Hours);
        Assert.Equal(60, result.Steps.Count);
        Assert.Equal(530, result.Summary.FinalStoredWh, 8);
    }

    public static IEnumerable<object[]> InvalidSettings()
    {
        var s = Request(0, 0).Settings;
        yield return [s with { PanelAreaM2 = -1 }];
        yield return [s with { PanelAreaM2 = double.NaN }];
        yield return [s with { BatteryCapacityWh = 0 }];
        yield return [s with { BatteryCapacityWh = double.PositiveInfinity }];
        yield return [s with { PanelEfficiency = 0 }];
        yield return [s with { PanelEfficiency = 20 }];
        yield return [s with { ConversionEfficiency = -.1 }];
        yield return [s with { ConversionEfficiency = double.NaN }];
        yield return [s with { InitialSoc = 50 }];
        yield return [s with { InitialSoc = -.1 }];
        yield return [s with { HourlyLoadWh = new double[23] }];
        yield return [s with { HourlyLoadWh = Enumerable.Repeat(-1.0, 24).ToArray() }];
        yield return [s with { HourlyLoadWh = Enumerable.Repeat(double.NaN, 24).ToArray() }];
    }

    [Theory, MemberData(nameof(InvalidSettings))]
    public void RejectsInvalidSettings(BatterySimulationSettings settings) =>
        Assert.Throws<ArgumentException>(() => BatterySimulator.Compute(Request(0, 0) with { Settings = settings }));

    public static IEnumerable<object[]> InvalidSeries()
    {
        var first = new ShadedIrradianceInterval("first", Start, Start.AddHours(1), 0);
        yield return [Array.Empty<ShadedIrradianceInterval>()];
        yield return [new[] { first with { MeanWm2 = double.NaN } }];
        yield return [new[] { first with { MeanWm2 = -1 } }];
        yield return [new[] { first with { End = Start } }];
        yield return [new[] { first with { Id = "" } }];
        yield return [new[] { first with { SourceStart = Start } }];
        yield return [new[] { first with { SourceStart = Start.AddMinutes(1), SourceEnd = first.End } }];
        yield return [new[] { first, first with { Start = first.End, End = first.End.AddHours(1) } }];
        yield return [new[] { first, new("gap", Start.AddHours(2), Start.AddHours(3), 0) }];
        yield return [new[] { first, new("overlap", Start.AddMinutes(30), Start.AddHours(2), 0) }];
    }

    [Theory, MemberData(nameof(InvalidSeries))]
    public void RejectsInvalidOrMissingSourceIntervals(ShadedIrradianceInterval[] intervals) =>
        Assert.Throws<InvalidDataException>(() => BatterySimulator.Compute(Request(0, 0) with { Irradiance = new(intervals, "UTC") }));

    [Fact]
    public void SnapshotsAreImmutableAndFingerprintIsDeterministic()
    {
        var values = Enumerable.Repeat(10.0, 24).ToArray();
        var intervals = new[] { new ShadedIrradianceInterval("one", Start, Start.AddHours(1), 0) };
        var input = Request(0, 0) with
        {
            Settings = Request(0, 0).Settings with { HourlyLoadWh = values },
            Irradiance = new(intervals, "UTC")
        };
        var result = BatterySimulator.Compute(input);
        values[0] = 999;
        intervals[0] = intervals[0] with { MeanWm2 = 999 };
        Assert.Equal(10, result.Input.Settings.HourlyLoadWh[0]);
        Assert.Equal(0, result.Input.Irradiance.Intervals[0].MeanWm2);
        Assert.Throws<NotSupportedException>(() => ((IList<BatteryHour>)result.Hours)[0] = result.Hours[0]);
        Assert.Equal(result.InputFingerprint, BatterySimulator.Compute(input).InputFingerprint);
        Assert.NotEqual(result.InputFingerprint, BatterySimulator.Compute(input with { Settings = input.Settings with { InitialSoc = 1 } }).InputFingerprint);
    }

    [Fact]
    public void EquivalentOffsetsAreContiguousAndOverflowIsRejected()
    {
        var request = Request(0, 10) with { Irradiance = new([
            new("one", Start, Start.AddHours(1), 0),
            new("two", Start.AddHours(1).ToOffset(TimeSpan.FromHours(7)), Start.AddHours(2), 0)], "UTC") };
        Assert.Equal(480, BatterySimulator.Compute(request).Summary.FinalStoredWh);
        request = Request(double.MaxValue, 0);
        Assert.Throws<ArithmeticException>(() => BatterySimulator.Compute(request with { Settings = request.Settings with { PanelAreaM2 = 2 } }));
        Assert.Throws<ArgumentException>(() => BatterySimulator.Compute(request with { Irradiance = request.Irradiance with { TimeZoneId = "" } }));
    }

    [Fact]
    public void RandomizedIntervalsConserveEnergyAndNeverExceedCapacity()
    {
        var random = new Random(743);
        var rows = Enumerable.Range(0, 4000).Select(i =>
            new ShadedIrradianceInterval(i.ToString(), Start.AddMinutes(i * 17), Start.AddMinutes((i + 1) * 17), random.NextDouble() * 1500)).ToArray();
        var result = BatterySimulator.Compute(new(new(2, .21, .87,
            Enumerable.Range(0, 24).Select(_ => random.NextDouble() * 450).ToArray(), 730, .38), new(rows, "Asia/Ho_Chi_Minh")));
        foreach (var step in result.Steps)
        {
            Assert.InRange(step.EndStoredWh, 0, 730);
            Assert.InRange(step.UnmetLoadWh, 0, step.LoadWh);
            Assert.Equal(step.LoadWh, step.ServedLoadWh + step.UnmetLoadWh, 8);
            Assert.Equal(step.StartStoredWh + step.PvWh, step.EndStoredWh + step.ServedLoadWh + step.CurtailedWh, 8);
        }
        var s = result.Summary;
        Assert.Equal(s.InitialStoredWh + s.PvWh, s.FinalStoredWh + s.ServedLoadWh + s.CurtailedWh, 6);
    }
}
