using System.Text.Json;
using SolarShade.Irradiance;

namespace SolarShade.Desktop;

/// <summary>Publishes selected user data from the verified, saved successful update.</summary>
internal static class IrradianceDataExport
{
    private const string PanelResults = "06-shading/panel-results.json";
    private const string FactorSemantics = "Shading factor=shaded/unshaded irradiance (1=open, 0=blocked); blank when the unshaded component is zero.";

    // The callback permits testing failures and changes after generation but before atomic publication.
    internal static void Publish(DebugDataRun run, string destination, Action<string>? afterWorkbook = null)
    {
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
            lock (run.SyncRoot)
            {
                snapshot = run.ExportState();
                using var inputs = SnapshotExport.LockInputs(snapshot.Settings);
                snapshot = run.ExportState();
                SnapshotExport.CopyVerified(Path.Combine(run.DirectoryPath, PanelResults), savedResults, snapshot.ArtifactHashes[PanelResults]);
            }
            using var document = JsonDocument.Parse(File.ReadAllText(savedResults));
            var root = document.RootElement;
            string workbook = Path.Combine(staging, "irradiance.xlsx");
            WriteWorkbook(workbook, root, snapshot);
            File.Delete(savedResults);
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
                    Model = root.GetProperty("Model").GetString(), TimeZoneId = root.GetProperty("TimeZoneId").GetString(),
                    TimestampConvention = root.GetProperty("TimestampConvention").GetString(), FactorSemantics,
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

    internal static void WriteWorkbook(string path, JsonElement root, DebugDataRun.Manifest snapshot)
    {
        string model = Text(root, "Model") switch {
            "Isotropic" => "Isotropic", "HayDavies" => "Hay–Davies",
            _ => throw Incomplete()
        };
        string zone = Text(root, "TimeZoneId"), convention = Text(root, "TimestampConvention");
        if (!root.TryGetProperty("Rows", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            throw Incomplete();
        Number(root, "BeforeEnergy"); Number(root, "AfterEnergy");
        // Validate every saved interval before writing any workbook data.
        foreach (var row in rows.EnumerateArray())
        {
            Instant(row, "Timestamp");
            var start = Instant(row, "Start"); var end = Instant(row, "End");
            if (end <= start) throw Incomplete();
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
                DatasetFingerprint = root.TryGetProperty("DatasetFingerprint", out var fingerprint) && fingerprint.ValueKind == JsonValueKind.String ? fingerprint.GetString() : null,
                SourceArtifactSha256 = snapshot.ArtifactHashes.GetValueOrDefault(PanelResults),
                NativeCadence = root.TryGetProperty("NativeCadence", out var cadence) && cadence.ValueKind == JsonValueKind.String ? cadence.GetString() : null,
                Source = root.TryGetProperty("Source", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null
            });
        ScientificSheet sheet = new("Irradiance",
            ["Timeseries", "Direct (W/m²)", $"Diffuse {model} (W/m²)",
                "Shading factor direct", "Shading factor diffuse", "Final total (W/m²)"],
            rows.EnumerateArray().Select(row => new object?[] {
                Instant(row, "Timestamp"), Number(row, "BeforeDirect"), Number(row, "BeforeDiffuse"),
                Factor(Number(row, "BeforeDirect"), Number(row, "AfterDirect")),
                Factor(Number(row, "BeforeDiffuse"), Number(row, "AfterDiffuse")), Number(row, "AfterTotal")
            })) { ColumnWidths = [36, 24, 30, 24, 24, 24] };
        ScientificWorkbook.Write(path, [sheet], metadata);
    }

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
