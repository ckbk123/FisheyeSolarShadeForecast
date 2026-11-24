using SolarShade.Irradiance;
using SolarShade.ShadingCorrection;

namespace SolarShade.Desktop;

/// <summary>Frozen source metadata and final panel values, never the mutable visible controls.</summary>
public sealed class IrradianceSnapshot
{
    internal Evaluation Evaluation { get; }
    private readonly UserSettings settings;
    public UserSettings Settings => settings with { };
    public PanelRun Run { get; }
    public string Id { get; }
    public string ContentFingerprint { get; }
    public string TimeZoneId => settings.Zone;
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    internal IrradianceSnapshot(Evaluation evaluation)
    {
        Evaluation = evaluation; settings = evaluation.Settings with { };
        Run = evaluation.Run with { Rows = Array.AsReadOnly(evaluation.Run.Rows.ToArray()) };
        Start = Run.Rows[0].Start;
        End = Run.Rows[^1].End;
        Id = evaluation.ManagedDebugRun!.Id;
        ContentFingerprint = AppData.Key(new { Run.Rows, Run.Model, Run.Panel, Start, End, TimeZoneId });
    }
}

public sealed record SourceReadiness(IrradianceSnapshot? Snapshot, string Reason)
{
    public bool IsReady => Snapshot != null;
}

/// <summary>One shared readiness contract for downstream services and navigation.</summary>
public static class IrradianceReadiness
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Evaluation, SourceReadiness> snapshots = new();
    public static SourceReadiness Inspect(IIrradianceService service, Evaluation? evaluation)
    {
        if (evaluation == null) return new(null, "Complete and update Solar Irradiance first.");
        if (!service.IsCurrent(evaluation) || evaluation.ManagedDebugRun?.HasCompleteDataset != true)
            return new(null, "Irradiance inputs or saved results changed. Update Solar Irradiance.");
        return snapshots.GetValue(evaluation, Build);
    }
    private static SourceReadiness Build(Evaluation evaluation)
    {
        try
        {
            ValidatePanel(evaluation.Run, StudyPeriod.FromSource(evaluation.Settings, evaluation.Raw));
            if (evaluation.Mask == null) return new(null, "A complete shaded result requires a sky mask.");
            return new(new(evaluation), "");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { return new(null, ex.Message); }
    }
    internal static void ValidatePanel(PanelRun run, StudyPeriod period)
    {
        var (first, end) = period;
        if (run.Rows.Count == 0 || run.Rows[0]?.Start != first || run.Rows[^1]?.End != end)
            throw new InvalidDataException("Shaded irradiance must cover the entire selected study period.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cursor = first;
        foreach (var row in run.Rows)
        {
            if (row == null || row.Start != cursor || row.End <= row.Start ||
                string.IsNullOrWhiteSpace(row.IntervalId) || !ids.Add(row.IntervalId) ||
                row.AfterTotal is not { } value || !double.IsFinite(value) || value < 0)
                throw new InvalidDataException("Shaded irradiance contains missing, invalid, duplicated or noncontiguous intervals.");
            cursor = row.End;
        }
    }
}
