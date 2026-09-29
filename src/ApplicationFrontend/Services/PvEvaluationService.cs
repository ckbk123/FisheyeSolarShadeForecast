using SolarShade.PvBattery;
using SolarShade.PvBattery.IO;
using SolarShade.PvBattery.Integration;

namespace SolarShade.Desktop;

public sealed class PvEvaluationService
{
    private readonly IrradianceWorkspaceController source;
    private readonly Func<IrradianceSnapshot, BatterySimulationSettings, CancellationToken, BatterySimulationResult> compute;
    public DependentResultStore Store { get; }
    public PvEvaluationService(IrradianceWorkspaceController source,
        Func<IrradianceSnapshot, BatterySimulationSettings, CancellationToken, BatterySimulationResult>? compute = null)
    {
        this.source = source;
        this.compute = compute ?? ((s, settings, ct) => PanelBatterySimulation.Compute(s.Run, settings, s.TimeZoneId, ct));
        Store = source.Service.CreateDependentStore("07-battery", AppData.Key(new { Schema = "pv-workspace-v1",
            Core = AppData.LibraryVersion(typeof(BatterySimulator)), IO = AppData.LibraryVersion(typeof(BatteryFiles)),
            Integration = AppData.LibraryVersion(typeof(PanelBatterySimulation)) }), "battery-hourly.csv", "battery-result.json", "battery-hourly.xlsx");
    }
    public Task<BatterySimulationResult> Evaluate(IrradianceSnapshot snapshot, BatterySimulationSettings settings, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!source.IsCurrent(snapshot)) throw new InvalidOperationException("Update Solar Irradiance first.");
        BatterySimulator.ValidateSettings(settings);
        var result = compute(snapshot, settings, ct);
        ct.ThrowIfCancellationRequested();
        if (!source.IsCurrent(snapshot)) throw new InvalidOperationException("Irradiance changed during evaluation.");
        Store.Publish(snapshot, AppData.Key(settings), folder =>
        {
            BatteryFiles.WriteCsv(Path.Combine(folder, "battery-hourly.csv"), result, ct);
            BatteryFiles.WriteJson(Path.Combine(folder, "battery-result.json"), result, ct);
            BatteryFiles.WriteXlsx(Path.Combine(folder, "battery-hourly.xlsx"), result, ct);
        }, ct);
        return result;
    }, ct);
}
