using System.Text.Json.Serialization;

namespace SolarShade.PvBattery;

/// <summary>Efficiencies and initial SoC are fractions, not percentages. Load slots are Wh per nominal local hour.</summary>
public sealed record BatterySimulationSettings(
    [property: JsonRequired] double PanelAreaM2,
    [property: JsonRequired] double PanelEfficiency,
    [property: JsonRequired] double ConversionEfficiency,
    IReadOnlyList<double> HourlyLoadWh,
    [property: JsonRequired] double BatteryCapacityWh,
    [property: JsonRequired] double InitialSoc)
{
    private IReadOnlyList<double> load =
        Array.AsReadOnly((HourlyLoadWh ?? throw new ArgumentNullException(nameof(HourlyLoadWh))).ToArray());
    [JsonRequired] public IReadOnlyList<double> HourlyLoadWh
    {
        get => load;
        init => load = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
}

/// <summary>Already shaded, plane-of-panel interval mean; never an accumulated Wh/m² value.</summary>
public sealed record ShadedIrradianceInterval(
    [property: JsonRequired] string Id, [property: JsonRequired] DateTimeOffset Start,
    [property: JsonRequired] DateTimeOffset End, [property: JsonRequired] double MeanWm2)
{
    public DateTimeOffset? SourceStart { get; init; }
    public DateTimeOffset? SourceEnd { get; init; }
    public DateTimeOffset? SourceLabel { get; init; }
}

public sealed record ShadedIrradianceSeries(IReadOnlyList<ShadedIrradianceInterval> Intervals, [property: JsonRequired] string TimeZoneId)
{
    private IReadOnlyList<ShadedIrradianceInterval> intervals =
        Array.AsReadOnly((Intervals ?? throw new ArgumentNullException(nameof(Intervals))).ToArray());
    [JsonRequired] public IReadOnlyList<ShadedIrradianceInterval> Intervals
    {
        get => intervals;
        init => intervals = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }
    public string? SourceDescription { get; init; }
    public string? SourceFingerprint { get; init; }
}

public sealed record BatterySimulationRequest(
    [property: JsonRequired] BatterySimulationSettings Settings,
    [property: JsonRequired] ShadedIrradianceSeries Irradiance);

public sealed record BatteryStep(
    string SourceIntervalId, DateTimeOffset Start, DateTimeOffset End,
    double IrradianceWm2, double AverageLoadW, double PvWh, double LoadWh,
    double ServedLoadWh, double UnmetLoadWh, double CurtailedWh,
    double StartStoredWh, double EndStoredWh, double EndSocPercent);

public sealed record BatteryHour(
    DateTimeOffset Start, DateTimeOffset End, bool IsPartialHour,
    double StartStoredWh, double EndStoredWh, double EndSocPercent,
    double PvWh, double LoadWh, double ServedLoadWh, double UnmetLoadWh, double CurtailedWh);

public sealed record BatterySummary(
    double InitialStoredWh, double FinalStoredWh, double MinimumSocPercent,
    double PvWh, double LoadWh, double ServedLoadWh, double UnmetLoadWh, double CurtailedWh,
    int HoursContainingShortfall, DateTimeOffset? FirstShortfallIntervalStart)
{
    public double? UnmetEnergyFraction => LoadWh > 0 ? UnmetLoadWh / LoadWh : null;
    public double StoredEnergyChangeWh => FinalStoredWh - InitialStoredWh;
}

/// <summary>Snapshot of inputs and results. Hour timestamps are ends of actual elapsed intervals.</summary>
public sealed class BatterySimulationResult
{
    /// <summary>Rehydrates a verified saved result without running the battery model.</summary>
    public static BatterySimulationResult Restore(string fingerprint, BatterySimulationRequest input,
        IReadOnlyList<BatteryStep> steps, IReadOnlyList<BatteryHour> hours, BatterySummary summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(hours); ArgumentNullException.ThrowIfNull(summary);
        BatterySimulator.ValidateSettings(input.Settings);
        if (hours.Count == 0 || steps.Count == 0) throw new InvalidDataException("Saved battery result is empty.");
        return new(fingerprint, input, steps, hours, summary);
    }
    public string ModelVersion { get; } = BatterySimulator.ModelVersion;
    public string Assumptions { get; } = "Fixed panel efficiency; conversion applied to PV only; ideal battery charge/discharge; " +
        "all supplied capacity accessible; no power limits; no daily reset; energy-based SoC; " +
        "constant irradiance within each source interval; load repeats by local civil hour.";
    public string InputFingerprint { get; }
    public BatterySimulationRequest Input { get; }
    public IReadOnlyList<BatteryStep> Steps { get; }
    public IReadOnlyList<BatteryHour> Hours { get; }
    public BatterySummary Summary { get; }

    internal BatterySimulationResult(string fingerprint, BatterySimulationRequest input,
        IEnumerable<BatteryStep> steps, IEnumerable<BatteryHour> hours, BatterySummary summary)
    {
        InputFingerprint = fingerprint;
        Input = input;
        Steps = Array.AsReadOnly(steps.ToArray());
        Hours = Array.AsReadOnly(hours.ToArray());
        Summary = summary;
    }
}
