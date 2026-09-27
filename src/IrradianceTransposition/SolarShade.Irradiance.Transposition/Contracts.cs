namespace SolarShade.Irradiance.Transposition;

public enum DiffuseModel { HayDavies, PerezDriesse, Isotropic }

/// <summary>Tilt from horizontal (0 faces up, 90 vertical); front-normal azimuth clockwise from true north.</summary>
public readonly record struct PanelOrientation(double TiltDegrees, double AzimuthDegrees)
{
    internal void Validate()
    {
        Guard.Range(TiltDegrees, 0, 90, nameof(TiltDegrees));
        Guard.Range(AzimuthDegrees, 0, 360, nameof(AzimuthDegrees));
    }
}

/// <summary>Zenith from vertical up, azimuth clockwise from true north, in degrees.
/// Hay/Perez expect apparent zenith. No refraction is silently applied: use FromGeometricNoaa
/// once for geometric inputs, or supply already apparent angles directly.</summary>
public readonly record struct SunPosition(double ZenithDegrees, double AzimuthDegrees)
{
    /// <summary>Explicit standard-atmosphere approximation from NOAA's solar calculation details.
    /// Do not apply to already apparent zenith. Actual refraction varies with local atmospheric conditions.</summary>
    public static SunPosition FromGeometricNoaa(double geometricZenithDegrees, double azimuthDegrees)
    {
        var geometric = new SunPosition(geometricZenithDegrees, azimuthDegrees);
        geometric.Validate();
        double elevation = 90 - geometricZenithDegrees;
        if (elevation > 85) return geometric;
        double tangent = Math.Tan(elevation * Math.PI / 180);
        double arcSeconds;
        if (elevation > 5)
            arcSeconds = 58.1 / tangent - .07 / Math.Pow(tangent, 3) + .000086 / Math.Pow(tangent, 5);
        else if (elevation > -.575)
            arcSeconds = 1735 + elevation * (-518.2 + elevation * (103.4 + elevation * (-12.79 + elevation * .711)));
        else arcSeconds = -20.774 / tangent;
        return new(geometricZenithDegrees - arcSeconds / 3600, azimuthDegrees);
    }

    internal void Validate()
    {
        Guard.Range(ZenithDegrees, 0, 180, nameof(ZenithDegrees));
        Guard.Range(AzimuthDegrees, 0, 360, nameof(AzimuthDegrees));
    }
}

/// <summary>Midpoint quadrature over the actual averaging window relative to the provider's label.</summary>
public sealed record SamplingWindow(double StartOffsetMinutes, double DurationMinutes, int Samples = 60)
{
    public static SamplingWindow Instant { get; } = new(0, 0, 1);
    public static SamplingWindow FollowingHour(int samples = 60) => new(0, 60, samples);
    public static SamplingWindow PrecedingHour(int samples = 60) => new(-60, 60, samples);
    public static SamplingWindow CenteredHour(int samples = 60) => new(-30, 60, samples);
    public static SamplingWindow? ForService(IrradianceService service, int samples = 60) => service switch
    {
        IrradianceService.NasaPower => FollowingHour(samples),
        IrradianceService.OpenMeteo or IrradianceService.Cams or IrradianceService.CopernicusCds => PrecedingHour(samples),
        _ => null // An unknown provider's averaging convention must be chosen explicitly.
    };
    internal void Validate()
    {
        Guard.Range(StartOffsetMinutes, -1440, 1440, nameof(StartOffsetMinutes));
        Guard.Range(DurationMinutes, 0, 1440, nameof(DurationMinutes));
        if (Samples is < 1 or > 10000 || (DurationMinutes == 0 && Samples != 1))
            throw new ArgumentOutOfRangeException(nameof(Samples));
    }
}

[Flags]
public enum TranspositionFlags
{
    None = 0,
    InferredDni = 1,
    LowSun = 2,
    NightDiffuseIsotropic = 4,
    HorizontalIdentity = 8
}

public sealed record TranspositionOptions
{
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    /// <summary>Reject unstable BHI inversion below this average daylight cosine. No artificial beam is created.</summary>
    public double MinimumMeanCosine { get; init; } = 1e-6;
    /// <summary>Explicit plausibility bound for inferred DNI, W/m². Exceeding it fails the batch; values are never capped.</summary>
    public double MaximumInferredDni { get; init; } = 1500;
    /// <summary>Serial by default: the solar callback need not be thread safe. >1 opts in; 0 selects a conservative CPU/memory limit.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;
    /// <summary>Caller-supplied provenance (provider, site, solar-angle convention); saved in workbook metadata.</summary>
    public string Provenance { get; init; } = "Solar angle convention supplied by caller.";
}

/// <summary>Unshaded front-side irradiance. Sky diffuse excludes ground reflection; this is not PV electrical output.</summary>
public readonly record struct PlaneIrradiance(double Direct, double SkyDiffuse, TranspositionFlags Flags)
{
    public double Total => Direct + SkyDiffuse;
}

public sealed record TransposedSample(DateTimeOffset Timestamp, double Direct, double SkyDiffuse,
    double? DirectFactor, double? DiffuseFactor, double? EffectiveDni, double? MeanDaylightCosine,
    TranspositionFlags Flags)
{
    public double Total => Direct + SkyDiffuse;
}

public sealed record TranspositionResult(int Status, string Message, IReadOnlyList<TransposedSample> Samples,
    string? OutputPath = null)
{
    public bool Succeeded => Status == 1;
}

internal static class Guard
{
    public static void Range(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"Must be finite and in [{minimum}, {maximum}].");
    }
    public static void Irradiance(double value, string name) => Range(value, 0, double.MaxValue, name);
}
