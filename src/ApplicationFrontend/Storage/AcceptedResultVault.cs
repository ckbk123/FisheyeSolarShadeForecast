using System.Security.Cryptography;
using System.Text.Json;
using SolarShade.Irradiance;
using SolarShade.Shading;
using SolarShade.ShadingCorrection;

namespace SolarShade.Desktop;

/// <summary>Verified complete results for an exact input configuration. No scientific work runs on restoration.</summary>
internal static class AcceptedResultVault
{
    private const string Owner = "SolarShade.AcceptedResult.v1";
    private sealed record SavedEvaluation(PanelRun Run, IrradianceDataset Raw, string Note,
        double TotalMilliseconds, int PreparationCount, SunPathOverlayResult? SunPath,
        CardinalDirectionOverlayResult? Cardinals, int SunPathGenerationCount, int CardinalGenerationCount);
    private sealed record VaultManifest(string Owner, string Key, string RunSha256, string EvaluationSha256,
        Dictionary<string, string> ArtifactHashes);

    internal static string Key(UserSettings s)
    {
        var sources = InputDependencies.Capture(s);
        return AppData.Key(new
        {
            Software = AppServices.SoftwareFingerprint,
            Photo = sources.Image.Content, Profile = sources.Profile.Content, Weather = sources.Weather.Content,
            Mask = sources.EditedMask.Content, MaskMetadata = sources.EditedMetadata.Content, s.SelectedMaskId,
            s.Model, s.Resolution, s.CenteredDisk, s.CoverageAngle,
            s.BottomAzimuth, s.CameraTilt, s.CameraRoll, s.PanelTilt, s.PanelAzimuth,
            s.Latitude, s.Longitude, s.Elevation, s.Start, s.End, s.Zone,
            s.Provider, s.ImportWindow, s.ImportIntervalMinutes, s.Substeps, s.Isotropic
        });
    }
    private static string Folder(string key) => Path.Combine(AppData.PathFor("AcceptedResults"), key);
    private static string BlobPath(string hash) => Path.Combine(AppData.PathFor("AcceptedResults"), "Blobs", hash);
    internal static (int Studies, long Bytes) StorageUsage()
    {
        string root = AppData.PathFor("AcceptedResults"); DebugDataStore.SafePath(root);
        if (!Directory.Exists(root)) return (0, 0);
        int count = Directory.EnumerateDirectories(root).Count(p =>
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), "^[A-F0-9]{64}$"));
        long bytes = DebugDataStore.Files(root).Sum(p => new FileInfo(p).Length);
        return (count, bytes);
    }
    /// <summary>Explicit cleanup of recognized vault entries; leaves unfamiliar files untouched.</summary>
    internal static int ClearKnown()
    {
        string root = AppData.PathFor("AcceptedResults"); DebugDataStore.SafePath(root);
        if (!Directory.Exists(root)) return 0;
        int removed = 0;
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(folder);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-F0-9]{64}$")) continue;
            var owner = AppData.Read<VaultManifest>(Path.Combine(folder, "vault.json"));
            if (owner?.Owner != Owner || owner.Key != name) continue;
            DebugDataStore.DeleteTree(root, folder); removed++;
        }
        string blobs = Path.Combine(root, "Blobs");
        if (Directory.Exists(blobs) && !Directory.EnumerateDirectories(root).Any(p =>
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), "^[A-F0-9]{64}$")))
            foreach (string file in Directory.EnumerateFiles(blobs))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^[A-F0-9]{64}$"))
                { DebugDataStore.SafePath(file); File.Delete(file); }
        return removed;
    }
    internal static void Archive(Evaluation result)
    {
        var run = result.ManagedDebugRun ?? throw new InvalidOperationException("No accepted run to archive.");
        if (!run.HasCompleteDataset) throw new InvalidOperationException("Only a complete shaded run can be archived.");
        string key = Key(result.Settings), destination = Folder(key);
        if (Directory.Exists(destination)) { Verify(destination, key); return; }
        string parent = Path.GetDirectoryName(destination)!;
        string staging = Path.Combine(parent, "." + key + "." + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            Directory.CreateDirectory(staging);
            DebugDataRun.Manifest snapshot;
            lock (run.SyncRoot)
            {
                using var inputs = SnapshotExport.LockInputs(result.Settings);
                snapshot = run.ExportState();
                foreach (var (relative, hash) in snapshot.ArtifactHashes)
                    StoreBlob(Path.Combine(run.DirectoryPath, relative), hash);
                File.Copy(Path.Combine(run.DirectoryPath, "run.json"), Path.Combine(staging, "run.json"));
            }
            var evaluation = new SavedEvaluation(result.Run, result.Raw, result.Note, result.TotalMilliseconds,
                result.PreparationCount, result.SunPath, result.Cardinals, result.SunPathGenerationCount, result.CardinalGenerationCount);
            AppData.Write(Path.Combine(staging, "evaluation.json"), evaluation);
            AppData.Write(Path.Combine(staging, "vault.json"), new VaultManifest(Owner, key,
                AppData.FileKey(Path.Combine(staging, "run.json")), AppData.FileKey(Path.Combine(staging, "evaluation.json")),
                snapshot.ArtifactHashes));
            Verify(staging, key);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) DebugDataStore.DeleteTree(parent, staging); }
    }
    internal static bool Exists(UserSettings settings) => Directory.Exists(Folder(Key(settings)));
    internal static (DebugDataRun Run, Evaluation Evaluation)? Restore(DebugDataStore store, UserSettings settings)
    {
        string key = Key(settings), folder = Folder(key);
        if (!Directory.Exists(folder)) return null;
        var (vault, saved) = Verify(folder, key);
        var manifest = AppData.Read<DebugDataRun.Manifest>(Path.Combine(folder, "run.json"))!;
        if (manifest.Status != "Complete" || manifest.Invalidated != ArtifactGroup.None ||
            manifest.Software != AppServices.SoftwareFingerprint || manifest.Kind != "calculation")
            throw new InvalidDataException("Archived result is not a complete result from this software version.");
        var inputs = InputDependencies.Capture(settings);
        lock (store.Gate)
        {
            store.Check();
            int updateNumber = Math.Max(manifest.UpdateNumber,
                (AppData.Read<DebugDataRun.Manifest>(Path.Combine(store.Root, "run.json"))?.UpdateNumber ?? 0) + 1);
            store.Current?.Invalidate(ArtifactGroup.All, inputs.SourcePaths);
            foreach (var (relative, hash) in vault.ArtifactHashes)
                CopyVerified(BlobPath(hash), Path.Combine(store.Root, relative), hash);
            // The archived settings may contain paths from a previous portable-folder location.
            manifest.Settings = PortablePaths.Map(settings, path => PortablePaths.Store(path));
            manifest.Keys = inputs.Keys.ToDictionary();
            manifest.Id = Guid.NewGuid().ToString("N");
            manifest.UpdateNumber = updateNumber;
            manifest.UpdatedUtc = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(store.Root);
            AppData.Write(Path.Combine(store.Root, "run.json"), manifest);
            store.Current = DebugDataRun.Restore(store);
            if (!store.Current.HasCompleteDataset)
                throw new InvalidDataException("Restored result did not pass complete-dataset verification.");
            var result = new Evaluation(saved.Run, saved.Raw, settings with { }, null, saved.Note,
                saved.TotalMilliseconds, saved.PreparationCount)
            {
                DebugDirectory = store.Root, SunPath = saved.SunPath, Cardinals = saved.Cardinals,
                SunPathGenerationCount = saved.SunPathGenerationCount, CardinalGenerationCount = saved.CardinalGenerationCount,
                Dependencies = inputs, ManagedDebugRun = store.Current
            };
            return (store.Current, result);
        }
    }
    private static (VaultManifest Manifest, SavedEvaluation Evaluation) Verify(string folder, string key)
    {
        DebugDataStore.SafePath(folder);
        var vault = AppData.Read<VaultManifest>(Path.Combine(folder, "vault.json"))
            ?? throw new InvalidDataException("Accepted-result metadata is missing.");
        if (vault.Owner != Owner || vault.Key != key || vault.ArtifactHashes == null ||
            vault.RunSha256 != AppData.FileKey(Path.Combine(folder, "run.json")) ||
            vault.EvaluationSha256 != AppData.FileKey(Path.Combine(folder, "evaluation.json")))
            throw new InvalidDataException("Accepted-result metadata has changed.");
        foreach (var (relative, hash) in vault.ArtifactHashes)
        {
            string path = Path.GetFullPath(Path.Combine(folder, relative));
            if (!DebugDataStore.Within(folder, path) || InputDependencies.ForArtifact(relative) == ArtifactGroup.None ||
                !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[A-F0-9]{64}$") ||
                !File.Exists(BlobPath(hash)) ||
                AppData.FileKey(BlobPath(hash)) != hash)
                throw new InvalidDataException("An accepted-result artifact has changed: " + relative);
        }
        var saved = AppData.Read<SavedEvaluation>(Path.Combine(folder, "evaluation.json"))
            ?? throw new InvalidDataException("Accepted-result data is missing.");
        return (vault, saved);
    }
    private static void CopyVerified(string source, string destination, string expected)
    {
        DebugDataStore.SafePath(source); DebugDataStore.SafePath(destination);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(input)) != expected) throw new InvalidDataException("Result artifact changed: " + source);
        input.Position = 0; Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        if (AppData.FileKey(destination) != expected) throw new IOException("Copied result artifact failed verification.");
    }
    private static void StoreBlob(string source, string hash)
    {
        string destination = BlobPath(hash);
        if (File.Exists(destination))
        {
            if (AppData.FileKey(destination) != hash) throw new InvalidDataException("A saved result blob has changed.");
            return;
        }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            CopyVerified(source, temporary, hash);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination) && AppData.FileKey(destination) == hash) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
