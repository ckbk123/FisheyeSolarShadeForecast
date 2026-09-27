namespace SolarShade.Irradiance;

// Keep numeric IDs stable; retired IDs 1 and 2 are intentionally undefined.
public enum IrradianceService { NasaPower = 0, OpenMeteo = 3, Nsrdb = 4, Cams = 5, Oikolab = 6, CopernicusCds = 7 }

/// <summary>Dates are machine-local calendar boundaries: start inclusive, end exclusive.</summary>
public sealed record IrradianceRequest(DateOnly StartDate, DateOnly EndDate,
    double Longitude, double Latitude, IrradianceService Service);

/// <summary>Native provider time label, expressed as a UTC instant; both components are horizontal W/m².</summary>
public readonly record struct IrradianceSample(DateTimeOffset TimestampUtc,
    double DirectHorizontal, double DiffuseHorizontal);

public sealed record HardwareCapabilities(int LogicalProcessors, long AvailableMemoryBytes,
    bool VectorAcceleration, int RecommendedConcurrency)
{
    public static HardwareCapabilities Detect()
    {
        long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new(Environment.ProcessorCount, memory, System.Numerics.Vector.IsHardwareAccelerated,
            Environment.ProcessorCount >= 4 && memory >= 1_073_741_824 ? 2 : 1);
    }
}

public sealed record IrradianceOptions
{
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public int MaxConcurrency { get; init; } = HardwareCapabilities.Detect().RecommendedConcurrency;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public string OpenMeteoModel { get; init; } = "era5";
    public string? NsrdbApiKey { get; init; }
    public string? NsrdbEmail { get; init; }
    public string? CamsEmail { get; init; }
    public string? OikolabApiKey { get; init; }
    public string? CdsPersonalAccessToken { get; init; }
    public TimeSpan CdsJobTimeout { get; init; } = TimeSpan.FromMinutes(20);
}

public sealed record IrradianceResult(int Status, string Message,
    IReadOnlyList<IrradianceSample> Samples, string TimeZoneId, string TimestampConvention,
    string? OutputPath = null)
{
    public bool Succeeded => Status == 1;
    /// <summary>Explicit selected-endpoint metadata. Null means callers must supply interval semantics.</summary>
    public TimeSpan? NativeCadence { get; init; }
    public TimestampLabel? Label { get; init; }
    public IrradianceValueKind ValueKind { get; init; } = IrradianceValueKind.IntervalMean;
}
