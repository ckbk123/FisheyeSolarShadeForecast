namespace SolarShade.Irradiance;

public static partial class IrradianceDatasetFiles
{
    private static readonly string[] Headers = ["Timestamp", "Direct horizontal (W/m²)", "Diffuse horizontal (W/m²)", "Interval ID", "Source start", "Source end", "Selected start", "Selected end", "Timestamp label", "Native cadence minutes", "Value kind", "Units", "Source", "Time zone", "Timestamp convention", "Fetched UTC", "Latitude", "Longitude"];

    /// <summary>Exports the returned values and their authoritative interval metadata; no recalculation or provider calls.</summary>
    public static IrradianceDataset Export(IrradianceDataset data, string outputXlsx, CancellationToken ct = default)
    {
        IrradianceDatasets.Validate(data);
        ScientificWorkbook.Write(outputXlsx, "Horizontal irradiance", Headers, data.Intervals.Select(r => new object?[]
        { r.Timestamp, r.DirectHorizontal, r.DiffuseHorizontal, r.Id, r.SourceStart, r.SourceEnd, r.Start, r.End,
            data.Label.ToString(), data.NativeCadence?.TotalMinutes, data.ValueKind.ToString(), data.Units,
            data.Source, data.TimeZoneId, data.TimestampConvention, data.FetchedUtc, data.Latitude, data.Longitude }),
            "Authoritative source interval means. Numeric irradiance W/m². Selected bounds may clip source bounds; source values retain original averaging semantics. " + data.TimestampConvention, ct);
        return data with { OutputPath = Path.GetFullPath(outputXlsx) };
    }

    /// <summary>Self-describing interval bounds override fallback settings. Legacy three-column files require explicit cadence and label.</summary>
    public static IrradianceDataset Import(string path, TimeZoneInfo zone, TimeSpan fallbackCadence,
        TimestampLabel fallbackLabel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rows = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? Csv(path) : Xlsx(path);
        if (rows.Count < 2) throw new InvalidDataException("Expected timestamp, BHI and DHI header and data rows.");
        string header = string.Join(" ", rows[0].Take(3)).ToLowerInvariant();
        if (new[] { "dni", "direct normal", "on panel", "tilted", "shaded", "factor" }.Any(header.Contains))
            throw new InvalidDataException("Import requires raw horizontal BHI and DHI, not DNI or corrected panel data.");
        if (new[] { "wh/", "wh /", "j/", "j /", "accumulated", "instantaneous" }.Any(header.Contains))
            throw new InvalidDataException("Import requires interval means in W/m²; accumulated energy and instantaneous observations need an explicit conversion.");
        bool native = rows[0].Length >= 16 && rows[0].Take(16).SequenceEqual(Headers.Take(16));
        if (!native && rows[0].Any(h => h is "Interval ID" or "Source start" or "Selected start" or "Value kind"))
            throw new InvalidDataException("Incomplete or changed native interval metadata. Preserve its complete exported schema.");
        var samples = new List<IrradianceSample>(); var intervals = new List<IrradianceInterval>();
        IrradianceDataset? template = null;
        for (int i = 1; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var cells = rows[i]; if (cells.All(string.IsNullOrWhiteSpace)) continue;
            if (cells.Length < 3 || !double.TryParse(cells[1], System.Globalization.NumberStyles.Float, Inv, out var bhi) ||
                !double.TryParse(cells[2], System.Globalization.NumberStyles.Float, Inv, out var dhi))
                throw new InvalidDataException($"Row {i+1}: numeric BHI and DHI required.");
            var timestamp = native ? NativeTime(cells[0]) : ParseTime(cells[0], zone);
            if (!native) { samples.Add(new(timestamp, bhi, dhi)); continue; }
            if (cells.Length < 16) throw new InvalidDataException($"Row {i+1}: incomplete interval metadata.");
            var current = new IrradianceDataset([], cells[12], cells[13], cells[14], Enum.Parse<TimestampLabel>(cells[8]),
                string.IsNullOrWhiteSpace(cells[9]) ? null : TimeSpan.FromMinutes(double.Parse(cells[9], Inv)), Enum.Parse<IrradianceValueKind>(cells[10]))
                { Units = cells[11], FetchedUtc = NativeTime(cells[15]),
                    Latitude = cells.Length > 16 && !string.IsNullOrWhiteSpace(cells[16]) ? double.Parse(cells[16], Inv) : null,
                    Longitude = cells.Length > 17 && !string.IsNullOrWhiteSpace(cells[17]) ? double.Parse(cells[17], Inv) : null };
            if (template != null && (current.Label != template.Label || current.NativeCadence != template.NativeCadence ||
                current.ValueKind != template.ValueKind || current.Units != template.Units || current.TimeZoneId != template.TimeZoneId ||
                current.Source != template.Source || current.TimestampConvention != template.TimestampConvention ||
                current.Latitude != template.Latitude || current.Longitude != template.Longitude || current.FetchedUtc != template.FetchedUtc))
                throw new InvalidDataException($"Row {i+1}: inconsistent dataset metadata.");
            template ??= current;
            intervals.Add(new(cells[3], timestamp, NativeTime(cells[4]), NativeTime(cells[5]),
                NativeTime(cells[6]), NativeTime(cells[7]), bhi, dhi));
        }
        var data = native ? (template ?? throw new InvalidDataException("No input rows.")) with { Intervals = Array.AsReadOnly(intervals.ToArray()) }
            : IrradianceDatasets.FromSamples(samples, fallbackCadence, fallbackLabel, "Imported: " + Path.GetFileName(path), zone.Id,
                $"User-specified {fallbackCadence.TotalMinutes} minute interval means; {fallbackLabel} label.");
        IrradianceDatasets.Validate(data); return data;
    }

    private static DateTimeOffset NativeTime(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"(Z|[+-]\d{2}:?\d{2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("Native interval metadata requires explicit UTC offsets on all timestamps and bounds; preserve the exported instants.");
        return ParseTime(value, TimeZoneInfo.Utc);
    }
}
