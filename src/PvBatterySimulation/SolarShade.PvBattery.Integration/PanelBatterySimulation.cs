using System.Text.Json;
using SolarShade.ShadingCorrection;

namespace SolarShade.PvBattery.Integration;

/// <summary>Consumes the existing final shaded panel result, without recomputing tilt or shading.</summary>
public static class PanelBatterySimulation
{
    public static ShadedIrradianceSeries FromPanelRun(PanelRun run, string? timeZoneId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ct.ThrowIfCancellationRequested();
        var intervals = new List<ShadedIrradianceInterval>(run.Rows.Count);
        foreach (var row in run.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (row is null || row.AfterTotal is not { } total)
                throw new InvalidDataException("Final shaded panel irradiance is unavailable; complete panel shading first.");
            intervals.Add(new(row.IntervalId, row.Start, row.End, total)
            {
                SourceStart = row.SourceStart, SourceEnd = row.SourceEnd, SourceLabel = row.Timestamp
            });
        }
        var series = new ShadedIrradianceSeries(intervals, timeZoneId ?? run.TimeZoneId ?? "")
        {
            SourceFingerprint = run.DatasetFingerprint,
            SourceDescription = JsonSerializer.Serialize(new { run.Source, run.Model, run.Panel, run.SkyCoverage,
                run.TimeZoneId, run.TimestampConvention, run.NativeCadence, run.SolarConvention })
        };
        BatterySimulator.ValidateSeries(series, ct);
        return series;
    }

    public static BatterySimulationResult Compute(PanelRun run, BatterySimulationSettings settings,
        string? timeZoneId = null, CancellationToken ct = default) =>
        BatterySimulator.Compute(new(settings, FromPanelRun(run, timeZoneId, ct)), ct);
}
