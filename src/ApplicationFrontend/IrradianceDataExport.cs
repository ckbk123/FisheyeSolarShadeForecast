using System.Text.Json;
using SolarShade.Irradiance;

namespace SolarShade.Desktop;

/// <summary>Publishes selected user data from the verified, saved successful update.</summary>
internal static class IrradianceDataExport
{
    private const string PanelResults = "06-shading/panel-results.json";
    private const string HorizontalResults = "03-irradiance/horizontal-irradiance.xlsx";
    private const string FactorSemantics = "Shading factor=shaded/unshaded irradiance (1=open, 0=blocked); blank when the unshaded component is zero.";

    // The callback permits testing failures and changes after generation but before atomic publication.
    internal static void Publish(DebugDataRun run, string destination, Action<string>? afterWorkbook = null, DataExportOptions? options = null)
    {
        options ??= new();
        options.Validate();
        destination = Path.GetFullPath(destination);
        if (DebugDataStore.Within(run.DirectoryPath, destination) || DebugDataStore.Within(destination, run.DirectoryPath))
            throw new ArgumentException("Choose an export destination outside Debug Data.");
        DebugDataStore.SafePath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The export destination already exists.");
        string parent = Path.GetDirectoryName(destination)!;
        string staging = Path.Combine(parent, ".solarshade-export-" + Guid.NewGuid().ToString("N") + ".partial");
        DateTimeOffset exported = DateTimeOffset.UtcNow;
        try
        {
            DebugDataRun.Manifest snapshot;
            string savedResults = Path.Combine(staging, "panel-results.json");
            string savedHorizontal = Path.Combine(staging, "horizontal-irradiance.xlsx");
            lock (run.SyncRoot)
            {
                snapshot = run.ExportState();
                using var inputs = SnapshotExport.LockInputs(snapshot.Settings);
                snapshot = run.ExportState();
                SnapshotExport.CopyVerified(Path.Combine(run.DirectoryPath, PanelResults), savedResults, snapshot.ArtifactHashes[PanelResults]);
                if (options.IncludesWeather)
                {
                    if (!snapshot.ArtifactHashes.TryGetValue(HorizontalResults, out var hash))
                        throw new InvalidOperationException("This study has no saved horizontal weather data. Update results before exporting weather columns.");
                    SnapshotExport.CopyVerified(Path.Combine(run.DirectoryPath, HorizontalResults), savedHorizontal, hash);
                }
            }
            using var document = JsonDocument.Parse(File.ReadAllText(savedResults));
            var root = document.RootElement;
            string workbook = Path.Combine(staging, "irradiance.xlsx");
            var weather = options.IncludesWeather
                ? IrradianceDatasetFiles.Import(savedHorizontal, TimeZoneInfo.Utc, TimeSpan.Zero, TimestampLabel.Explicit) : null;
            WriteWorkbook(workbook, root, snapshot, options, weather);
            File.Delete(savedResults);
            if (weather != null) File.Delete(savedHorizontal);
            afterWorkbook?.Invoke(workbook);
            lock (run.SyncRoot)
            {
                using var inputs = SnapshotExport.LockInputs(snapshot.Settings);
                var current = run.ExportState();
                if (current.Id != snapshot.Id || current.ArtifactHashes.Count != snapshot.ArtifactHashes.Count ||
                    snapshot.ArtifactHashes.Any(p => !current.ArtifactHashes.TryGetValue(p.Key, out var hash) || hash != p.Value))
                    throw new InvalidOperationException("Results changed during export. Export the new completed update.");
                AppData.Write(Path.Combine(staging, "export.json"), new {
                    Status = "Complete", ResultId = snapshot.Id, ExportedUtc = exported,
                    Products = new[] { "Irradiance" }, Workbook = "irradiance.xlsx", WorkbookSha256 = AppData.FileKey(workbook),
                    SourceArtifactSha256 = snapshot.ArtifactHashes[PanelResults],
                    SelectedFields = options.Fields.ToString(), options.SourceIntervalBounds, WeatherSource = weather?.Source,
                    WeatherArtifactSha256 = weather == null ? null : snapshot.ArtifactHashes[HorizontalResults],
                    Model = root.GetProperty("Model").GetString(), TimeZoneId = root.GetProperty("TimeZoneId").GetString(),
                    TimestampConvention = root.GetProperty("TimestampConvention").GetString(), FactorSemantics, snapshot.EquipmentAngles,
                    RowCount = root.GetProperty("Rows").GetArrayLength()
                });
                DebugDataStore.SafePath(destination);
                Directory.Move(staging, destination);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) DebugDataStore.DeleteTree(parent, staging);
        }
    }

    internal static void WriteWorkbook(string path, JsonElement root, DebugDataRun.Manifest snapshot,
        DataExportOptions? options = null, IrradianceDataset? weather = null)
    {
        options ??= new();
        options.Validate();
        string model = Text(root, "Model") switch {
            "Isotropic" => "Isotropic", "HayDavies" => "Hay–Davies",
            _ => throw Incomplete()
        };
        string zone = Text(root, "TimeZoneId"), convention = Text(root, "TimestampConvention");
        if (!root.TryGetProperty("Rows", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            throw Incomplete();
        Number(root, "BeforeEnergy"); Number(root, "AfterEnergy");
        if (options.IncludesWeather)
        {
            if (weather == null) throw Incomplete();
            // Weather files may extend beyond the study and the local study day may clip a source interval.
            weather = IrradianceDatasets.Select(weather, Instant(rows[0], "Start"), Instant(rows[rows.GetArrayLength() - 1], "End"));
            if (weather.Intervals.Count != rows.GetArrayLength()) throw IntervalMismatch();
            if (options.Fields.HasFlag(IrradianceFields.GlobalHorizontal) && !weather.Intervals.Any(r => r.GlobalHorizontal.HasValue))
                throw new InvalidOperationException("This study has no original GHI. Export its horizontal components or download weather again and update.");
        }
        // Validate every saved interval before writing any workbook data.
        int index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var timestamp = Instant(row, "Timestamp");
            var start = Instant(row, "Start"); var end = Instant(row, "End");
            if (end <= start) throw Incomplete();
            if (weather != null)
            {
                var interval = weather.Intervals[index++];
                if (interval.Id != Text(row, "IntervalId") || interval.Timestamp != timestamp ||
                    interval.Start != start || interval.End != end ||
                    interval.SourceStart != Instant(row, "SourceStart") || interval.SourceEnd != Instant(row, "SourceEnd"))
                    throw IntervalMismatch();
            }
            double direct = Number(row, "BeforeDirect"), diffuse = Number(row, "BeforeDiffuse");
            double afterDirect = Number(row, "AfterDirect"), afterDiffuse = Number(row, "AfterDiffuse");
            Number(row, "AfterTotal");
            Factor(direct, afterDirect); Factor(diffuse, afterDiffuse);
        }
        string metadata = "Panel irradiance interval means in W/m². Direct and diffuse are unshaded components on the panel; " +
            "final total irradiance is the saved shaded direct plus shaded sky diffuse. No ground reflection. " +
            "Unknown sky is blocked in the final total. Timeseries preserves every saved source timestamp label and its UTC offset. " +
            FactorSemantics + $" Diffuse model: {model}. Provenance: " + JsonSerializer.Serialize(new {
                ResultId = snapshot.Id, Model = model, TimeZoneId = zone, TimestampConvention = convention,
                StudyStartDate = DateOnly.FromDateTime(snapshot.Settings.Start),
                StudyEndDateInclusive = DateOnly.FromDateTime(snapshot.Settings.End),
                EffectiveStart = Instant(rows[0], "Start"), EffectiveEnd = Instant(rows[rows.GetArrayLength() - 1], "End"),
                Panel = root.TryGetProperty("Panel", out var panel) && panel.ValueKind == JsonValueKind.Object ? panel : (JsonElement?)null,
                PanelStorageConvention = SolarShade.Irradiance.Transposition.PanelOrientation.SolverConvention,
                snapshot.EquipmentAngles,
                DatasetFingerprint = root.TryGetProperty("DatasetFingerprint", out var fingerprint) && fingerprint.ValueKind == JsonValueKind.String ? fingerprint.GetString() : null,
                SourceArtifactSha256 = snapshot.ArtifactHashes.GetValueOrDefault(PanelResults),
                WeatherArtifactSha256 = weather == null ? null : snapshot.ArtifactHashes.GetValueOrDefault(HorizontalResults),
                NativeCadence = root.TryGetProperty("NativeCadence", out var cadence) && cadence.ValueKind == JsonValueKind.String ? cadence.GetString() : null,
                Source = root.TryGetProperty("Source", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null
            });
        var columns = IrradianceExportColumn.ForModel(model).Where(c => options.Fields.HasFlag(c.Field)).ToArray();
        string[] timeHeaders = options.SourceIntervalBounds ? ["Timeseries", "Source interval start", "Source interval end"] : ["Timeseries"];
        List<ScientificSheet> sheets = [new("Irradiance", [.. timeHeaders, .. columns.Select(c => c.Header)],
            rows.EnumerateArray().Select((row, i) => Values(row, weather?.Intervals[i], columns, options.SourceIntervalBounds)))
            { ColumnWidths = [.. timeHeaders.Select(_ => 36d), .. columns.Select(c => c.Width)] }];
        if (weather != null)
        {
            sheets.Add(new("Weather source", ["Property", "Value"], [
                ["Source", weather.Source], ["Units", weather.Units], ["Time zone", weather.TimeZoneId],
                ["Timestamp label", weather.Label.ToString()], ["Interval convention", weather.TimestampConvention],
                ["Native cadence (minutes)", weather.NativeCadence?.TotalMinutes], ["Fetched at (UTC)", weather.FetchedUtc.ToUniversalTime()],
                ["Values", "Original source interval means on a horizontal surface, before panel transposition or shading."],
                ["Study boundaries", "An interval clipped at a study boundary keeps the value and timestamp of its source interval."]
            ]) { ColumnWidths = [30, 115] });
        }
        ScientificWorkbook.Write(path, sheets, metadata);
    }

    private static object?[] Values(JsonElement row, IrradianceInterval? weather, IrradianceExportColumn[] columns, bool sourceBounds)
    {
        int firstQuantity = sourceBounds ? 3 : 1;
        var values = new object?[firstQuantity + columns.Length];
        values[0] = Instant(row, "Timestamp");
        if (sourceBounds)
        {
            values[1] = weather?.SourceStart ?? Instant(row, "SourceStart");
            values[2] = weather?.SourceEnd ?? Instant(row, "SourceEnd");
        }
        for (int i = 0; i < columns.Length; i++)
            values[firstQuantity + i] = columns[i].Field switch
            {
                IrradianceFields.PanelDirect => Number(row, "BeforeDirect"),
                IrradianceFields.PanelDiffuse => Number(row, "BeforeDiffuse"),
                IrradianceFields.DirectFactor => Factor(Number(row, "BeforeDirect"), Number(row, "AfterDirect")),
                IrradianceFields.DiffuseFactor => Factor(Number(row, "BeforeDiffuse"), Number(row, "AfterDiffuse")),
                IrradianceFields.ShadedTotal => Number(row, "AfterTotal"),
                IrradianceFields.GlobalHorizontal => weather!.GlobalHorizontal,
                IrradianceFields.DirectHorizontal => weather!.DirectHorizontal,
                IrradianceFields.DiffuseHorizontal => weather!.DiffuseHorizontal,
                _ => throw new ArgumentOutOfRangeException()
            };
        return values;
    }

    private static InvalidOperationException IntervalMismatch() =>
        new("Saved weather intervals do not match the panel results. Update results before exporting.");

    private static string Text(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw Incomplete();
        return value.GetString()!;
    }
    private static double Number(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number)) throw Incomplete();
        return number;
    }
    private static DateTimeOffset Instant(JsonElement parent, string property)
    {
        string text = Text(parent, property);
        bool offset = text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-';
        if (!offset || !parent.GetProperty(property).TryGetDateTimeOffset(out var instant)) throw Incomplete();
        return instant;
    }
    private static double? Factor(double before, double after)
    {
        if (before == 0) return null;
        double value = after / before;
        if (!double.IsFinite(value)) throw Incomplete();
        return value;
    }
    private static InvalidOperationException Incomplete() => new("Saved irradiance results are incomplete or invalid. Update results before exporting.");
}
