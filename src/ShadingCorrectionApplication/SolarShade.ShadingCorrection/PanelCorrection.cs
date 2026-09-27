using System.Diagnostics;
using System.Text.Json;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;

namespace SolarShade.ShadingCorrection;

public sealed record PanelRow(DateTimeOffset Timestamp, DateTimeOffset Start, DateTimeOffset End,
    double BeforeDirect, double BeforeDiffuse, double? AfterDirect, double? AfterDiffuse, double? UpperTotal)
{
    public string IntervalId { get; init; } = "";
    public DateTimeOffset SourceStart { get; init; } = Start;
    public DateTimeOffset SourceEnd { get; init; } = End;
    public double BeforeTotal => BeforeDirect + BeforeDiffuse;
    public double? AfterTotal => AfterDirect + AfterDiffuse;
    public double Hours => (End - Start).TotalHours;
    public double? DirectTransmission => ShadingCorrectionModule.Transmission(BeforeDirect, AfterDirect);
    public double? DiffuseTransmission => ShadingCorrectionModule.Transmission(BeforeDiffuse, AfterDiffuse);
    public double? TotalTransmission => ShadingCorrectionModule.Transmission(BeforeTotal, AfterTotal);
    public double? LossFraction => TotalTransmission is { } value ? 1 - value : null;
}
public sealed record PanelVisibilityRow(string IntervalId, DateTimeOffset Timestamp, double Weight,
    FluxVisibility Direct, FluxVisibility IsotropicDiffuse);

public sealed record PanelRun(IReadOnlyList<PanelRow> Rows, double? SkyCoverage, double Milliseconds, string Model)
{
    private IReadOnlyList<PanelRow> rows = Array.AsReadOnly(Rows.ToArray());
    public IReadOnlyList<PanelRow> Rows
    {
        get => rows;
        init => rows = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public IReadOnlyList<string> ArtifactPaths { get; init; } = Array.Empty<string>();
    public PanelOrientation? Panel { get; init; }
    public string? Source { get; init; }
    public string? TimeZoneId { get; init; }
    public string? TimestampConvention { get; init; }
    public TimeSpan? NativeCadence { get; init; }
    public double? SourceLatitude { get; init; }
    public double? SourceLongitude { get; init; }
    public string? DatasetFingerprint { get; init; }
    public string? SolarConvention { get; init; }
    public IReadOnlyList<PanelVisibilityRow> Visibility { get; init; } = Array.Empty<PanelVisibilityRow>();
    public double BeforeEnergy => Rows.Sum(r => r.BeforeTotal * r.Hours) / 1000;
    public double? AfterEnergy => Rows.Any(r => r.AfterTotal is null) ? null : Rows.Sum(r => r.AfterTotal!.Value * r.Hours) / 1000;
    public double? EnergyTransmission => ShadingCorrectionModule.Transmission(BeforeEnergy, AfterEnergy);
    public double? LossFraction => EnergyTransmission is { } value ? 1 - value : null;
    public double? LossPercent => LossFraction * 100;
}

public static partial class ShadingCorrectionModule
{
    /// <summary>Stage entry point: computes and saves its required outputs before returning success.</summary>
    public static PanelRun ApplyToPanel(PreparedTranspositionResult source, PanelSkyScene? scene,
        string debugDirectory, CancellationToken ct = default)
    {
        var run = ApplyToPanel(source, scene, ct);
        return run with { ArtifactPaths = ExportPanel(run, debugDirectory, ct) };
    }
    /// <summary>Unitless after/before ratio. Undefined zero-baseline and unavailable results remain null.</summary>
    public static double? Transmission(double before, double? after) => before > 0 && after.HasValue ? after.Value / before : null;

    /// <summary>Applies receiver-plane visibility to each transposed component before temporal integration.
    /// Unknown sky is blocked in the main result and transparent in UpperTotal. Missing scenes produce no shaded curve.</summary>
    public static PanelRun ApplyToPanel(PreparedTranspositionResult source, PanelSkyScene? scene,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Model is not (DiffuseModel.HayDavies or DiffuseModel.Isotropic))
            throw new ArgumentException("Panel shading supports Hay-Davies or isotropic diffuse; ground reflection is excluded.", nameof(source));
        var timer = Stopwatch.StartNew();
        var normal = Direction.FromAngles(source.Panel.AzimuthDegrees, source.Panel.TiltDegrees);
        var dome = scene?.Dome(normal) ?? new FluxVisibility(1, 1);
        var rows = new PanelRow[source.Rows.Count];
        var visibility = new List<PanelVisibilityRow>();
        for (int i = 0; i < rows.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var input = source.Rows[i];
            double direct = 0, diffuse = 0, upper = 0;
            foreach (var sample in input.Samples)
            {
                ct.ThrowIfCancellationRequested();
                var sun = Direction.FromAngles(sample.Solar.AzimuthDegrees, sample.Solar.ApparentZenithDegrees);
                // Visibility is irrelevant when both direct and circumsolar contributions are zero.
                var disk = scene is null || (sample.Direct == 0 && sample.CircumsolarDiffuse == 0)
                    ? new FluxVisibility(1, 1) : scene.Disk(sun, normal);
                var weight = sample.Solar.Weight;
                direct += sample.Direct * disk.Visible * weight;
                diffuse += (sample.IsotropicDiffuse * dome.Visible + sample.CircumsolarDiffuse * disk.Visible) * weight;
                upper += (sample.Direct * (disk.Visible + 1 - disk.Known) +
                    sample.IsotropicDiffuse * (dome.Visible + 1 - dome.Known) +
                    sample.CircumsolarDiffuse * (disk.Visible + 1 - disk.Known)) * weight;
                if (scene != null) visibility.Add(new(input.Interval.Id, sample.Solar.Timestamp, weight, disk, dome));
            }
            rows[i] = new(input.Interval.Timestamp, input.Interval.Start, input.Interval.End,
                input.Direct, input.SkyDiffuse, scene is null ? null : direct,
                scene is null ? null : diffuse, scene is null ? null : upper)
            { IntervalId = input.Interval.Id, SourceStart = input.Interval.SourceStart, SourceEnd = input.Interval.SourceEnd };
        }
        return new(rows, scene is null ? null : dome.Known, timer.Elapsed.TotalMilliseconds, source.Model.ToString())
            { Visibility = visibility.AsReadOnly(), Panel = source.Panel, Source = source.Timeline.Dataset.Source,
                TimeZoneId = source.Timeline.Dataset.TimeZoneId, TimestampConvention = source.Timeline.Dataset.TimestampConvention,
                NativeCadence = source.Timeline.Dataset.NativeCadence, SourceLatitude = source.Timeline.Dataset.Latitude,
                SourceLongitude = source.Timeline.Dataset.Longitude, DatasetFingerprint = IrradianceDatasets.Fingerprint(source.Timeline.Dataset),
                SolarConvention = source.Timeline.Convention };
    }

    /// <summary>Writes exact returned panel values and per-substep visibility. No calculation is rerun.</summary>
    public static IReadOnlyList<string> ExportPanel(PanelRun run, string directory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        string metadata = "Panel irradiance in W/m²; timestamps preserve source labels and explicit source/effective bounds. " +
            "Transmission=shaded/unshaded (1=open,0=blocked), blank when baseline is zero. " +
            "Main result assumes unknown sky blocked; upper bound assumes unknown sky open. No ground reflection. " +
            "Provenance: " + JsonSerializer.Serialize(new { run.Panel, run.Source, run.TimeZoneId, run.TimestampConvention,
                run.NativeCadence, run.SourceLatitude, run.SourceLongitude, run.DatasetFingerprint, run.SolarConvention });
        void Write(string filename, string[] headers, IEnumerable<object?[]> values)
        {
            var path = Path.Combine(directory, filename);
            ScientificWorkbook.Write(path, "Panel results", headers, values, metadata + " Model=" + run.Model, ct);
            paths.Add(path);
        }
        if (run.Rows.All(r => r.AfterTotal != null))
        {
            Write("panel-shaded.xlsx", ["Timestamp", "Shaded direct on panel (W/m²)", "Shaded sky diffuse on panel (W/m²)", "Shaded total on panel (W/m²)", "Upper total (W/m²)", "Interval ID", "Source start", "Source end", "Interval start", "Interval end"],
                run.Rows.Select(r => new object?[] { r.Timestamp, r.AfterDirect, r.AfterDiffuse, r.AfterTotal, r.UpperTotal, r.IntervalId, r.SourceStart, r.SourceEnd, r.Start, r.End }));
            Write("shading-correction-factors.xlsx", ["Timestamp", "Direct transmission", "Sky diffuse transmission", "Total transmission", "Loss fraction", "Interval ID", "Source start", "Source end", "Interval start", "Interval end"],
                run.Rows.Select(r => new object?[] { r.Timestamp, r.DirectTransmission, r.DiffuseTransmission, r.TotalTransmission, r.LossFraction, r.IntervalId, r.SourceStart, r.SourceEnd, r.Start, r.End }));
            int part = 0;
            foreach (var rows in run.Visibility.Chunk(100_000))
                Write(part++ == 0 ? "shading-visibility.xlsx" : $"shading-visibility-{part:D3}.xlsx",
                    ["Interval ID", "Substep timestamp", "Quadrature weight", "Direct visible fraction", "Direct observed fraction", "Isotropic visible fraction", "Isotropic observed fraction"],
                    rows.Select(r => new object?[] { r.IntervalId, r.Timestamp, r.Weight, r.Direct.Visible, r.Direct.Known, r.IsotropicDiffuse.Visible, r.IsotropicDiffuse.Known }));
        }
        ct.ThrowIfCancellationRequested();
        var json = Path.Combine(directory, "panel-results.json");
        var temporary = json + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { run.Rows, run.Model, run.SkyCoverage,
                run.Panel, run.Source, run.TimeZoneId, run.TimestampConvention, run.NativeCadence,
                run.SourceLatitude, run.SourceLongitude, run.DatasetFingerprint, run.SolarConvention,
                run.BeforeEnergy, run.AfterEnergy, run.EnergyTransmission, run.LossFraction, run.LossPercent }, new JsonSerializerOptions { WriteIndented = true }));
            ct.ThrowIfCancellationRequested(); File.Move(temporary, json, true); paths.Add(json);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return paths.AsReadOnly();
    }
}
