namespace SolarShade.Irradiance.Transposition;

public sealed record TransposedSubstep(SolarGeometrySample Solar, double Direct, double IsotropicDiffuse,
    double CircumsolarDiffuse, TranspositionFlags Flags)
{
    public double SkyDiffuse => IsotropicDiffuse + CircumsolarDiffuse;
    public double Total => Direct + SkyDiffuse;
}
public sealed record TransposedInterval(IrradianceInterval Interval, IReadOnlyList<TransposedSubstep> Samples,
    double Direct, double SkyDiffuse, double EffectiveDni, double MeanDaylightCosine, TranspositionFlags Flags)
{
    public double Total => Direct + SkyDiffuse;
}
public sealed record PreparedTranspositionResult(SolarTimeline Timeline, PanelOrientation Panel, DiffuseModel Model,
    IReadOnlyList<TransposedInterval> Rows)
{
    public double BeforeEnergy => Rows.Sum(r => r.Total * r.Interval.Hours) / 1000;
    public string? OutputPath { get; init; }
}

public static partial class TranspositionModule
{
    /// <summary>Consumes the authoritative solar timeline. Whole-source samples infer DNI; selected samples integrate the requested slice.
    /// Direct/diffuse means assume constant daylight DNI and constant DHI within a source interval.</summary>
    public static PreparedTranspositionResult ComputePrepared(SolarTimeline timeline, PanelOrientation panel,
        DiffuseModel model = DiffuseModel.HayDavies, TranspositionOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(timeline); IrradianceDatasets.Validate(timeline.Dataset); panel.Validate();
        options ??= new();
        Guard.Range(options.MinimumMeanCosine, 1e-12, 1, nameof(options.MinimumMeanCosine));
        Guard.Range(options.MaximumInferredDni, 1, 10000, nameof(options.MaximumInferredDni));
        if (model is not (DiffuseModel.HayDavies or DiffuseModel.Isotropic))
            throw new ArgumentException("Prepared panel-shading components support Hay–Davies and isotropic; use Compute for unshaded Perez–Driesse.");
        if (timeline.Intervals.Count != timeline.Dataset.Intervals.Count) throw new ArgumentException("Solar timeline must preserve every selected source interval.");
        var kernel = new SkyTransposition.PanelKernel(panel);
        var result = new TransposedInterval[timeline.Intervals.Count];
        for (int i = 0; i < result.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var geometry = timeline.Intervals[i]; var row = geometry.Interval;
            if (row != timeline.Dataset.Intervals[i]) throw new ArgumentException("Solar/source interval mismatch.");
            ValidateSamples(geometry.SourceSamples, row.SourceStart, row.SourceEnd);
            ValidateSamples(geometry.Samples, row.Start, row.End);
            double meanCosine = geometry.SourceSamples.Sum(s => Math.Max(0, Math.Cos(s.ApparentZenithDegrees * SkyTransposition.Radians)) * s.Weight);
            double dni = InferDni(row.DirectHorizontal, meanCosine, options);
            var flags = row.DirectHorizontal > 0 ? TranspositionFlags.InferredDni : TranspositionFlags.None;
            var samples = new TransposedSubstep[geometry.Samples.Count];
            double direct = 0, diffuse = 0;
            for (int j = 0; j < samples.Length; j++)
            {
                ct.ThrowIfCancellationRequested(); var solar = geometry.Samples[j];
                var value = kernel.EvaluateComponents(new(solar.ApparentZenithDegrees, solar.AzimuthDegrees),
                    solar.ApparentZenithDegrees < 90 ? dni : 0, row.DiffuseHorizontal, SkyTransposition.ExtraterrestrialDni(solar.Timestamp), model);
                double isotropic = value.Isotropic, circumsolar = value.Circumsolar;
                if (panel.TiltDegrees == 0)
                {
                    // Same measured horizontal identity as the batch API, also when empirical near-horizon floors apply.
                    double scale = value.SkyDiffuse > 0 ? row.DiffuseHorizontal / value.SkyDiffuse : 0;
                    isotropic *= scale; circumsolar *= scale;
                    flags |= TranspositionFlags.HorizontalIdentity;
                }
                samples[j] = new(solar, value.Direct, isotropic, circumsolar, value.Flags);
                direct += value.Direct * solar.Weight; diffuse += (isotropic + circumsolar) * solar.Weight; flags |= value.Flags;
            }
            result[i] = new(row, Array.AsReadOnly(samples), direct, diffuse, dni, meanCosine, flags);
        }
        return new(timeline, panel, model, Array.AsReadOnly(result));
    }

    private static void ValidateSamples(IReadOnlyList<SolarGeometrySample> samples, DateTimeOffset start, DateTimeOffset end)
    {
        if (samples.Count == 0 || Math.Abs(samples.Sum(s => s.Weight) - 1) > 1e-10) throw new ArgumentException("Solar quadrature weights must sum to one.");
        DateTimeOffset? previous = null;
        foreach (var s in samples)
        {
            if (!double.IsFinite(s.Weight) || s.Weight <= 0 || s.Timestamp < start || s.Timestamp >= end || previous is { } p && s.Timestamp <= p)
                throw new ArgumentException("Invalid solar sample weight, order or represented interval.");
            new SunPosition(s.ApparentZenithDegrees, s.AzimuthDegrees).Validate(); previous = s.Timestamp;
        }
    }

    public static PreparedTranspositionResult ExportPrepared(PreparedTranspositionResult result, string outputXlsx, CancellationToken ct = default)
    {
        ScientificSheet main = new("Unshaded panel", ["Timestamp", "Direct on panel (W/m²)", "Sky diffuse on panel (W/m²)", "Total on panel (W/m²)", "Interval ID", "Source start", "Source end", "Selected start", "Selected end", "Effective DNI (W/m²)", "Mean daylight cosine", "Flags"],
            result.Rows.Select(r => new object?[] { r.Interval.Timestamp, r.Direct, r.SkyDiffuse, r.Total, r.Interval.Id,
                r.Interval.SourceStart, r.Interval.SourceEnd, r.Interval.Start, r.Interval.End, r.EffectiveDni, r.MeanDaylightCosine, r.Flags.ToString() }));
        // Keep the native main table stable. Diagnostics may exceed Excel's worksheet limit for fine source cadence.
        const int chunkSize = 100000;
        string[] detailHeaders = ["Interval ID", "Source timestamp", "Sample timestamp", "Weight", "Apparent zenith (degrees)", "Azimuth (degrees)", "Direct (W/m²)", "Isotropic diffuse (W/m²)", "Circumsolar diffuse (W/m²)", "Flags"];
        var sheets = new List<ScientificSheet> { main };
        long sampleCount = result.Rows.Sum(r => (long)r.Samples.Count);
        for (long offset = 0; offset < sampleCount; offset += chunkSize)
            sheets.Add(new(offset == 0 ? "Integration components" : $"Integration components {offset/chunkSize+1}", detailHeaders, DetailRows(result, offset, chunkSize)));
        ScientificWorkbook.Write(outputXlsx, sheets,
            $"Actual returned transposition values. Model {result.Model}; tilt {result.Panel.TiltDegrees}; azimuth {result.Panel.AzimuthDegrees}. Source: {result.Timeline.Dataset.Source}. Constant daylight DNI inferred over whole source interval; constant source DHI. Selected interval energy uses actual duration. No ground reflection. {result.Timeline.Convention}", ct);
        return result with { OutputPath = Path.GetFullPath(outputXlsx) };
    }

    private static IEnumerable<object?[]> DetailRows(PreparedTranspositionResult result, long offset, int count)
    {
        // Skip whole source rows before allocating cell arrays; memory stays bounded to the streaming writer.
        foreach (var row in result.Rows)
        {
            if (offset >= row.Samples.Count) { offset -= row.Samples.Count; continue; }
            for (int i = (int)offset; i < row.Samples.Count && count > 0; i++, count--)
            {
                var s = row.Samples[i];
                yield return [row.Interval.Id, row.Interval.Timestamp, s.Solar.Timestamp, s.Solar.Weight,
                    s.Solar.ApparentZenithDegrees, s.Solar.AzimuthDegrees, s.Direct, s.IsotropicDiffuse, s.CircumsolarDiffuse, s.Flags.ToString()];
            }
            offset = 0; if (count == 0) yield break;
        }
    }
}
