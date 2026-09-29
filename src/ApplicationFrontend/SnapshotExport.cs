using System.Security.Cryptography;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ApplicationFrontend.Tests")]

namespace SolarShade.Desktop;

/// <summary>Copies a verified published dataset; never invokes scientific computation or workbook exporters.</summary>
internal static class SnapshotExport
{
    internal static readonly string[] RequiredArtifacts = [
        "01-calibration/calibration.yml", "01-calibration/camera-profile.json",
        "02-sky-mask/sky-mask.png", "02-sky-mask/mask-details.json",
        "02-orientation/cardinal-directions.xlsx", "02-orientation/cardinal-directions-details.json", "02-orientation/cardinal-directions-overlay.png",
        "03-irradiance/horizontal-irradiance.xlsx", "04-solar-positions/solar-positions.xlsx", "04-solar-positions/solar-integration-samples.xlsx",
        "04-solar-positions/sun-path-details.json", "04-solar-positions/sun-path-overlay.png", "04-solar-positions/sun-path-projection.xlsx",
        "05-transposition/panel-unshaded.xlsx", "06-shading/panel-results.json", "06-shading/panel-shaded.xlsx",
        "06-shading/shading-correction-factors.xlsx", "06-shading/shading-visibility.xlsx"];

    internal static void Publish(DebugDataRun run, string destination, Action<string, DebugDataRun.Manifest, DateTimeOffset>? writePdf = null)
    {
        destination = Path.GetFullPath(destination);
        if (DebugDataStore.Within(run.DirectoryPath, destination) || DebugDataStore.Within(destination, run.DirectoryPath))
            throw new ArgumentException("Choose an export destination outside Debug Data.");
        DebugDataStore.SafePath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The export destination already exists.");
        string parent = Path.GetDirectoryName(destination)!;
        string staging = Path.Combine(parent, ".solarshade-export-" + Guid.NewGuid().ToString("N") + ".partial");
        DateTimeOffset exported = DateTimeOffset.UtcNow;
        DebugDataRun.Manifest snapshot;
        try
        {
            lock (run.SyncRoot)
            {
                snapshot = run.ExportState();
                using var inputs = LockInputs(snapshot.Settings);
                snapshot = run.ExportState(); // Check again after obtaining handles that prevent source writes/deletes.
                Directory.CreateDirectory(staging);
                foreach (var (relative, hash) in snapshot.ArtifactHashes)
                    CopyVerified(Path.Combine(run.DirectoryPath, relative), Path.Combine(staging, relative), hash);
                CopyVerified(Path.Combine(run.DirectoryPath, "run.json"), Path.Combine(staging, "run.json"), AppData.FileKey(Path.Combine(run.DirectoryPath, "run.json")));
            }
            ValidateResults(staging);
            (writePdf ?? SummaryPdf.Write)(staging, snapshot, exported);
            string pdf = Path.Combine(staging, "Summary.pdf");
            using (var document = PdfSharp.Pdf.IO.PdfReader.Open(pdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                if (document.PageCount != 1) throw new InvalidDataException("The summary must contain exactly one page.");
            lock (run.SyncRoot)
            {
                using var inputs = LockInputs(snapshot.Settings);
                var current = run.ExportState();
                if (current.Id != snapshot.Id) throw new InvalidOperationException("Results changed during export. Export the new completed update.");
                AppData.Write(Path.Combine(staging, "export.json"), new {
                    Status = "Complete", ResultId = snapshot.Id, ExportedUtc = exported,
                    SummarySha256 = AppData.FileKey(pdf), RunManifestSha256 = AppData.FileKey(Path.Combine(staging, "run.json")),
                    snapshot.ArtifactHashes
                });
                DebugDataStore.SafePath(destination);
                Directory.Move(staging, destination); // Same-volume rename exposes only the complete package.
            }
        }
        finally
        {
            // Only this operation's unguessable temporary directory may be removed.
            if (Directory.Exists(staging)) DebugDataStore.DeleteTree(parent, staging);
        }
    }

    private sealed class InputLocks : IDisposable
    {
        internal readonly List<FileStream> Handles = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }
    internal static IDisposable LockInputs(UserSettings settings)
    {
        var result = new InputLocks();
        try
        {
            foreach (string path in InputDependencies.Capture(settings).SourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            { DebugDataStore.SafePath(path); result.Handles.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    private static void CopyVerified(string source, string destination, string expected)
    {
        DebugDataStore.SafePath(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(input)) != expected) throw new IOException("A debug artifact changed during export: " + Path.GetFileName(source));
        input.Position = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        if (AppData.FileKey(destination) != expected) throw new IOException("Export verification failed: " + Path.GetFileName(source));
    }
    private static void ValidateResults(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "06-shading", "panel-results.json")));
        var root = document.RootElement;
        static bool Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n);
        if (!Number(root.GetProperty("BeforeEnergy")) || !Number(root.GetProperty("AfterEnergy")) ||
            root.GetProperty("Rows").GetArrayLength() == 0 || root.GetProperty("Rows").EnumerateArray().Any(r => !Number(r.GetProperty("AfterTotal"))))
            throw new InvalidOperationException("Shaded results are incomplete. Update results before exporting.");
    }
}
