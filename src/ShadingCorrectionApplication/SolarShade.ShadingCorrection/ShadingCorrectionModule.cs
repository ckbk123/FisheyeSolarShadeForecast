using SolarShade.Irradiance;
using SolarShade.Shading;

namespace SolarShade.ShadingCorrection;

/// <summary>Corrected horizontal irradiance in W/m². Null denotes an unknown component.</summary>
public readonly record struct CorrectedIrradianceSample(DateTimeOffset TimestampUtc,
    double? DirectHorizontal, double? DiffuseHorizontal)
{
    public double? TotalHorizontal => DirectHorizontal + DiffuseHorizontal;
}

/// <summary>Applies optical shading losses to horizontal irradiance, without file I/O.</summary>
public static partial class ShadingCorrectionModule
{
    /// <summary>Applies independent direct and diffuse factors (0 = open, 1 = blocked).</summary>
    public static CorrectedIrradianceSample Apply(IrradianceSample sample,
        double? directShadingFactor, double? diffuseShadingFactor) => new(
            sample.TimestampUtc,
            Correct(sample.DirectHorizontal, directShadingFactor, nameof(directShadingFactor)),
            Correct(sample.DiffuseHorizontal, diffuseShadingFactor, nameof(diffuseShadingFactor)));

    /// <summary>Matches the upstream outputs by instant and preserves irradiance order and timestamp labels.
    /// Requires exactly one shading row per irradiance sample; offsets may differ for the same instant.
    /// The shading result's shared diffuse factor is applied to every sample.</summary>
    public static IReadOnlyList<CorrectedIrradianceSample> Apply(
        IReadOnlyList<IrradianceSample> samples, ShadingResult shading)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(shading);
        ArgumentNullException.ThrowIfNull(shading.Rows);
        ArgumentNullException.ThrowIfNull(shading.Diffuse);
        if (samples.Count != shading.Rows.Count)
            throw new ArgumentException("Irradiance and shading must contain the same timestamps.", nameof(shading));

        var factors = new Dictionary<DateTimeOffset, double?>();
        foreach (var row in shading.Rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(row.Direct);
            if (!factors.TryAdd(row.Timestamp, row.Direct.ShadingFactor))
                throw new ArgumentException($"Duplicate shading timestamp: {row.Timestamp:O}.", nameof(shading));
        }

        var result = new CorrectedIrradianceSample[samples.Count];
        var seen = new HashSet<DateTimeOffset>();
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (!seen.Add(sample.TimestampUtc))
                throw new ArgumentException($"Duplicate irradiance timestamp: {sample.TimestampUtc:O}.", nameof(samples));
            if (!factors.TryGetValue(sample.TimestampUtc, out var direct))
                throw new ArgumentException($"Missing shading timestamp: {sample.TimestampUtc:O}.", nameof(shading));
            result[i] = Apply(sample, direct, shading.Diffuse.ShadingFactor);
        }
        return result;
    }

    private static double? Correct(double irradiance, double? factor, string factorName)
    {
        if (!double.IsFinite(irradiance) || irradiance < 0)
            throw new ArgumentOutOfRangeException(nameof(irradiance), "Irradiance must be finite and nonnegative.");
        if (factor is double value && (!double.IsFinite(value) || value < 0 || value > 1))
            throw new ArgumentOutOfRangeException(factorName, "Shading factor must be between 0 and 1, or null when unknown.");
        // Zero incoming energy remains zero even if visibility is unknown.
        return irradiance == 0 ? 0 : irradiance * (1 - factor);
    }
}
