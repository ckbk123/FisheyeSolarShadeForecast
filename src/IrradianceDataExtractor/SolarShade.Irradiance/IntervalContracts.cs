namespace SolarShade.Irradiance;

public enum TimestampLabel { Start, End, Center, Explicit }
public enum IrradianceValueKind { IntervalMean, AccumulatedEnergy, Instantaneous }

/// <summary>Authoritative source interval. Start/End explicitly select a subset without changing the source mean.</summary>
public sealed record IrradianceInterval(string Id, DateTimeOffset Timestamp,
    DateTimeOffset SourceStart, DateTimeOffset SourceEnd, DateTimeOffset Start, DateTimeOffset End,
    double DirectHorizontal, double DiffuseHorizontal)
{
    public double Hours => (End - Start).TotalHours;
}

public sealed record IrradianceDataset(IReadOnlyList<IrradianceInterval> Intervals, string Source,
    string TimeZoneId, string TimestampConvention, TimestampLabel Label, TimeSpan? NativeCadence,
    IrradianceValueKind ValueKind = IrradianceValueKind.IntervalMean)
{
    public DateTimeOffset FetchedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Units { get; init; } = "W/m²";
    public string? OutputPath { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

/// <summary>Portable solar sample. Weight integrates a mean, and sums to one within each sample sequence.</summary>
public sealed record SolarGeometrySample(DateTimeOffset Timestamp, double GeometricZenithDegrees,
    double ApparentZenithDegrees, double AzimuthDegrees, double Weight);
public sealed record SolarIntervalGeometry(IrradianceInterval Interval, SolarGeometrySample AtLabel,
    IReadOnlyList<SolarGeometrySample> SourceSamples, IReadOnlyList<SolarGeometrySample> Samples);
public sealed record SolarTimeline(IrradianceDataset Dataset, IReadOnlyList<SolarIntervalGeometry> Intervals,
    string Convention);

public static class IrradianceDatasets
{
    /// <summary>Content identity for validated source metadata and intervals; artifact destination is not scientific input.</summary>
    public static string Fingerprint(IrradianceDataset data)
    {
        Validate(data);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data with { OutputPath = null })));
    }
    public static IrradianceDataset SelectCalendarRange(IrradianceDataset data, DateTime startDate, DateTime endInclusiveDate, TimeZoneInfo zone)
        => Select(data, Boundary(startDate, zone), Boundary(endInclusiveDate.AddDays(1), zone));
    public static DateTimeOffset Boundary(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local))
            throw new ArgumentException("Midnight is ambiguous or missing in the selected time zone.");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static IrradianceDataset FromSamples(IReadOnlyList<IrradianceSample> samples, TimeSpan cadence,
        TimestampLabel label, string source, string timeZoneId, string convention)
    {
        if (cadence <= TimeSpan.Zero || label == TimestampLabel.Explicit || !Enum.IsDefined(label))
            throw new ArgumentException("A positive native cadence and start/end/center label are required.");
        long offset = label == TimestampLabel.End ? -cadence.Ticks : label == TimestampLabel.Center ? -cadence.Ticks / 2 : 0;
        var rows = samples.Select(s =>
        {
            var start = s.TimestampUtc.AddTicks(offset);
            return new IrradianceInterval(s.TimestampUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                s.TimestampUtc, start, start + cadence, start, start + cadence, s.DirectHorizontal, s.DiffuseHorizontal);
        }).ToArray();
        var data = new IrradianceDataset(Array.AsReadOnly(rows), source, timeZoneId, convention, label, cadence);
        Validate(data); return data;
    }

    /// <summary>Current selected provider endpoints return hourly means. Unsupported label conventions require explicit caller metadata.</summary>
    public static IrradianceDataset FromResult(IrradianceResult result, IrradianceService service)
    {
        if (!result.Succeeded) throw new InvalidDataException(result.Message);
        if (result.NativeCadence is not { } cadence || result.Label is not { } label || result.ValueKind != IrradianceValueKind.IntervalMean)
            throw new ArgumentException("Provider result must declare cadence, label and interval-mean values; ambiguous metadata requires explicit input.");
        return FromSamples(result.Samples, cadence, label, service.ToString(), result.TimeZoneId, result.TimestampConvention);
    }

    public static void Validate(IrradianceDataset data)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(data.Intervals);
        if (data.ValueKind != IrradianceValueKind.IntervalMean || data.Units != "W/m²")
            throw new ArgumentException("The pipeline requires interval-mean horizontal irradiance in W/m².");
        if (data.Intervals.Count == 0) throw new InvalidDataException("No irradiance intervals supplied.");
        if (data.NativeCadence is { } cadence && cadence <= TimeSpan.Zero) throw new ArgumentException("Invalid native cadence.");
        if (!Enum.IsDefined(data.Label)) throw new ArgumentException("Invalid timestamp label convention.");
        if (data.Latitude is { } lat && (!double.IsFinite(lat) || Math.Abs(lat) > 90) ||
            data.Longitude is { } lon && (!double.IsFinite(lon) || Math.Abs(lon) > 180)) throw new ArgumentException("Invalid source coordinates.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        IrradianceInterval? previous = null;
        foreach (var row in data.Intervals)
        {
            if (string.IsNullOrWhiteSpace(row.Id) || !ids.Add(row.Id)) throw new InvalidDataException("Duplicate or empty source interval ID.");
            if (!double.IsFinite(row.DirectHorizontal) || !double.IsFinite(row.DiffuseHorizontal) || row.DirectHorizontal < 0 || row.DiffuseHorizontal < 0)
                throw new InvalidDataException($"Invalid horizontal irradiance at {row.Timestamp:O}.");
            if (row.SourceEnd <= row.SourceStart || row.Start < row.SourceStart || row.End > row.SourceEnd || row.End <= row.Start)
                throw new InvalidDataException($"Invalid source/selected bounds at {row.Timestamp:O}.");
            if (data.NativeCadence is { } duration && row.SourceEnd - row.SourceStart != duration)
                throw new InvalidDataException($"Interval duration disagrees with native cadence at {row.Timestamp:O}.");
            if (data.Label == TimestampLabel.Start && row.Timestamp != row.SourceStart ||
                data.Label == TimestampLabel.End && row.Timestamp != row.SourceEnd ||
                data.Label == TimestampLabel.Center && row.Timestamp != row.SourceStart.AddTicks((row.SourceEnd - row.SourceStart).Ticks / 2))
                throw new InvalidDataException($"Timestamp disagrees with label convention at {row.Timestamp:O}.");
            if (previous != null && (row.Timestamp <= previous.Timestamp || row.SourceStart < previous.SourceEnd))
                throw new InvalidDataException("Source intervals must be ordered, unique and non-overlapping; gaps are preserved.");
            previous = row;
        }
    }

    public static IrradianceDataset Select(IrradianceDataset data, DateTimeOffset start, DateTimeOffset end)
    {
        Validate(data);
        if (end <= start) throw new ArgumentException("Selection end must follow start.");
        var rows = data.Intervals.Where(r => r.Start < end && r.End > start).Select(r => r with
        { Start = r.Start < start ? start : r.Start, End = r.End > end ? end : r.End }).ToArray();
        var cursor = start;
        foreach (var row in rows)
        {
            if (row.Start > cursor) throw new InvalidDataException($"Missing irradiance coverage [{cursor:O}, {row.Start:O}).");
            cursor = row.End;
        }
        if (cursor < end) throw new InvalidDataException($"Missing irradiance coverage [{cursor:O}, {end:O}).");
        return data with { Intervals = Array.AsReadOnly(rows), OutputPath = null };
    }
}
