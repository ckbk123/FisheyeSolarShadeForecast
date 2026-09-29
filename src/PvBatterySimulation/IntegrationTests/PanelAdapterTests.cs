using SolarShade.PvBattery.Integration;
using SolarShade.PvBattery.IO;
using SolarShade.ShadingCorrection;
using Xunit;

namespace SolarShade.PvBattery.IntegrationTests;

public class PanelAdapterTests
{
    private static PanelRun Source()
    {
        var start = new DateTimeOffset(2025, 1, 1, 6, 0, 0, TimeSpan.FromHours(7));
        return new([
            new(start.AddHours(1), start.AddMinutes(15), start.AddHours(1), 900, 100, 200, 50, 999)
                { IntervalId = "one", SourceStart = start, SourceEnd = start.AddHours(1) },
            new(start.AddHours(2), start.AddHours(1), start.AddHours(2), 900, 100, 0, 0, 999)
                { IntervalId = "two" }
        ], 1, 0, "Isotropic") { TimeZoneId = "Asia/Ho_Chi_Minh", DatasetFingerprint = "upstream", Source = "test fixture" };
    }

    [Fact]
    public void ActualExporterAndMemoryAdapterGiveIdenticalSimulation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pv-battery-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = Source();
            ShadingCorrectionModule.ExportPanel(run, directory);
            var memory = PanelBatterySimulation.FromPanelRun(run);
            var workbook = PanelWorkbookReader.Read(Path.Combine(directory, "panel-shaded.xlsx"), run.TimeZoneId!);
            Assert.Equal(memory.Intervals, workbook.Intervals);
            var settings = new BatterySimulationSettings(2, .2, .9, Enumerable.Repeat(100.0, 24).ToArray(), 1000, .5);
            var a = PanelBatterySimulation.Compute(run, settings);
            var b = BatterySimulator.Compute(new(settings, workbook));
            Assert.Equal(a.Hours, b.Hours);
            Assert.Equal(a.Steps, b.Steps);
            Assert.Equal(a.Summary, b.Summary);
            Assert.Equal(67.5, a.Summary.PvWh, 8); // 250 W/m² × 2 m² × .2 × .9 × .75 h.
            Assert.Equal(392.5, a.Summary.FinalStoredWh, 8);
            Assert.Equal("upstream", memory.SourceFingerprint);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingShadedValueCannotUseBeforeOrUpperTotal()
    {
        var run = Source();
        run = run with { Rows = [run.Rows[0] with { AfterDiffuse = null }] };
        Assert.Throws<InvalidDataException>(() => PanelBatterySimulation.FromPanelRun(run));
    }

    [Fact]
    public void RequiresStudyTimezoneUnlessCallerExplicitlyProvidesIt()
    {
        var run = Source() with { TimeZoneId = null };
        Assert.Throws<ArgumentException>(() => PanelBatterySimulation.FromPanelRun(run));
        Assert.Equal("UTC", PanelBatterySimulation.FromPanelRun(run, "UTC").TimeZoneId);
    }
}
