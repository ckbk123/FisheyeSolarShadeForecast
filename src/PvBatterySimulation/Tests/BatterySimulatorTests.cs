using SolarShade.PvBattery;
using Xunit;

namespace SolarShade.PvBattery.Tests;

public class BatterySimulatorTests
{
    internal static readonly DateTimeOffset Start = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static BatterySimulationRequest Request(double pv, double load, double initial = .5,
        double capacity = 1000, double hours = 1) => new(
        new(1, 1, 1, Enumerable.Repeat(load, 24).ToArray(), capacity, initial),
        new([new("row", Start, Start.AddHours(hours), pv)], "UTC"));

    [Theory]
    [InlineData(0, 0, .5, 500, 0, 0)]
    [InlineData(0, 100, .5, 400, 0, 0)]
    [InlineData(100, 100, .5, 500, 0, 0)]
    [InlineData(300, 100, .5, 700, 0, 0)]
    [InlineData(0, 90, .5, 410, 0, 0)]
    [InlineData(200, 0, .95, 1000, 0, 150)]
    [InlineData(0, 100, .1, 0, 0, 0)]
    [InlineData(0, 100, .05, 0, 50, 0)]
    [InlineData(200, 100, 0, 100, 0, 0)]
    public void AnalyticalEnergyCases(double pv, double load, double initial, double end, double unmet, double curtailed)
    {
        var result = BatterySimulator.Compute(Request(pv, load, initial));
        Assert.Equal(end, result.Summary.FinalStoredWh, 8);
        Assert.Equal(unmet, result.Summary.UnmetLoadWh, 8);
        Assert.Equal(curtailed, result.Summary.CurtailedWh, 8);
        Assert.Equal(end / 10, Assert.Single(result.Hours).EndSocPercent, 8);
        Assert.Equal(initial * 1000 + pv, end + result.Summary.ServedLoadWh + curtailed, 8);
    }

    [Fact]
    public void BothEfficienciesApplyToPvExactlyOnce()
    {
        var input = Request(800, 100);
        input = input with { Settings = input.Settings with { PanelAreaM2 = 2, PanelEfficiency = .2, ConversionEfficiency = .9 } };
        var result = BatterySimulator.Compute(input);
        Assert.Equal(288, result.Summary.PvWh, 8);
        Assert.Equal(68.8, Assert.Single(result.Hours).EndSocPercent, 8);
        input = input with { Irradiance = input.Irradiance with { Intervals = [new("night", Start, Start.AddHours(1), 0)] } };
        Assert.Equal(400, BatterySimulator.Compute(input).Summary.FinalStoredWh, 8);
    }

    [Fact]
    public void ActualDurationAndLocalLoadBoundariesAreUsed()
    {
        var request = Request(0, 100, hours: .25);
        var partial = Assert.Single(BatterySimulator.Compute(request).Hours);
        Assert.Equal(475, partial.EndStoredWh);
        Assert.True(partial.IsPartialHour);
        double[] profile = new double[24]; profile[10] = 100; profile[11] = 200;
        request = request with {
            Settings = request.Settings with { HourlyLoadWh = profile },
            Irradiance = new([new("span", Start.AddHours(10.5), Start.AddHours(11.5), 0)], "UTC")
        };
        var result = BatterySimulator.Compute(request);
        Assert.Equal(350, result.Summary.FinalStoredWh);
        Assert.Equal(2, result.Hours.Count);
        Assert.Equal(Start.AddHours(11), result.Hours[0].End);
        Assert.All(result.Hours, h => Assert.True(h.IsPartialHour));
    }

    [Fact]
    public void SubhourSaturationCannotBeAveragedAway()
    {
        var request = Request(0, 0, initial: 1, capacity: 100);
        // Same load slot throughout: charge 100 Wh in first half, discharge 100 Wh in second.
        request = request with { Settings = request.Settings with { HourlyLoadWh = Enumerable.Repeat(200.0, 24).ToArray() },
            Irradiance = new([
                new("first", Start, Start.AddMinutes(30), 400),
                new("second", Start.AddMinutes(30), Start.AddHours(1), 0)], "UTC") };
        var result = BatterySimulator.Compute(request);
        Assert.Equal(0, Assert.Single(result.Hours).EndStoredWh);
        Assert.Equal(100, result.Summary.CurtailedWh);
        Assert.Equal(0, result.Summary.UnmetLoadWh);
    }

    [Fact]
    public void CarriesEnergyAcrossDaysAndContinuesAfterDepletion()
    {
        var result = BatterySimulator.Compute(Request(0, 10, hours: 72));
        Assert.Equal(72, result.Hours.Count);
        Assert.Equal(260, result.Hours[23].EndStoredWh);
        Assert.Equal(20, result.Hours[47].EndStoredWh);
        Assert.Equal(220, result.Summary.UnmetLoadWh);
    }

    [Fact]
    public void CancelledRunDoesNotReturnSuccess()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => BatterySimulator.Compute(Request(0, 0), cancel.Token));
    }
}
