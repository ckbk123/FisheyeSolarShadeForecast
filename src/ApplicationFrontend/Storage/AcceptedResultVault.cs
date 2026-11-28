using System.Security.Cryptography;
using System.Text.Json;
using SolarShade.Irradiance;
using SolarShade.Shading;
using SolarShade.ShadingCorrection;
using SolarShade.SkyPhotoMasking;

namespace SolarShade.Desktop;

/// <summary>Verified complete results for an exact input configuration. No scientific work runs on restoration.</summary>
internal static class AcceptedResultVault
{
    private const string Owner = "SolarShade.AcceptedResult.v1";
    private sealed record SavedEvaluation(PanelRun Run, IrradianceDataset Raw, string Note,
        double TotalMilliseconds, int PreparationCount, SunPathOverlayResult? SunPath,
        CardinalDirectionOverlayResult? Cardinals, int SunPathGenerationCount, int CardinalGenerationCount);
    private sealed record VaultManifest(string Owner, string Key, string RunSha256, string EvaluationSha256,
        Dictionary<string, string> ArtifactHashes)
    {
        public string? PhotoSha256 { get; init; }
    }

    internal static string Key(UserSettings s) => Key(s, InputDependencies.Capture(s));

    private static string Key(UserSettings s, InputDependencies sources)
    {
        bool services = WeatherSourceSelection.UsesServices(s);
        string baseline = AppData.Key(new
        {
            Software = AppServices.SoftwareFingerprint,
            UseDataServices = services,
            Photo = sources.Image.Content, Profile = sources.Profile.Content, Weather = sources.Weather.Content,
            Mask = sources.EditedMask.Content, MaskMetadata = sources.EditedMetadata.Content, s.SelectedMaskId,
            MaskAncestor = sources.MaskAncestor.Content,
            s.Model, s.Resolution, s.CenteredDisk, s.CoverageAngle,
            s.BottomAzimuth, s.CameraTilt, s.CameraRoll, s.PanelTilt, s.PanelAzimuth,
            s.Latitude, s.Longitude, s.Elevation, Start = services ? s.Start : (DateTime?)null,
            End = services ? s.End : (DateTime?)null, s.Zone,
            Provider = services ? s.Provider : (int?)null, s.ImportWindow, s.ImportIntervalMinutes, s.Substeps, s.Isotropic
        });
        if (s.ApparentFullFovDegrees.HasValue) return AppData.Key(new { baseline, s.ApparentFullFovDegrees });
        return baseline;
    }
    private static string Folder(string key) => Path.Combine(AppData.PathFor("AcceptedResults"), key);
    private static string BlobPath(string hash) => Path.Combine(AppData.PathFor("AcceptedResults"), "Blobs", hash);
    internal static (int Studies, long Bytes) StorageUsage()
    {
        string root = AppData.PathFor("AcceptedResults"); DebugDataStore.SafePath(root);
        if (!Directory.Exists(root)) return (0, 0);
        int count = PhotoStorageCleanup.Directories(root).Count(p => PhotoStorageCleanup.IsHash(Path.GetFileName(p)) &&
            ManagedVaultDirectory.Find(p, Owner, Path.GetFileName(p)) != null);
        long bytes = DebugDataStore.Files(root).Sum(p => new FileInfo(p).Length);
        return (count, bytes);
    }
    /// <summary>Explicit cleanup of recognized vault entries; leaves unfamiliar files untouched.</summary>
    internal static int ClearKnown()
    {
        var entries = ReadVaults(out bool referencesKnown);
        var cleanup = new PhotoStorageCleanup(new("", ""), []);
        RemovePhotoArchives(cleanup, entries, [], referencesKnown);
        string blobs = AppData.PathFor(Path.Combine("AcceptedResults", "Blobs"));
        DebugDataStore.SafePath(blobs);
        if (referencesKnown && Directory.Exists(blobs))
            foreach (string blob in Directory.EnumerateFiles(blobs))
            {
                string hash = Path.GetFileName(blob);
                if (!PhotoStorageCleanup.IsHash(hash)) continue;
                DebugDataStore.SafePath(blob);
                if (AppData.FileKey(blob) == hash) cleanup.RemoveFile(blobs, hash);
            }
        return entries.Count;
    }
    internal static void Archive(Evaluation result)
    {
        var run = result.ManagedDebugRun ?? throw new InvalidOperationException("No accepted run to archive.");
        if (!run.HasCompleteDataset) throw new InvalidOperationException("Only a complete shaded run can be archived.");
        string key = Key(result.Settings), baseFolder = Folder(key);
        var existing = ManagedVaultDirectory.Find(baseFolder, Owner, key);
        if (existing != null) { Verify(existing, key); return; }
        string destination = ManagedVaultDirectory.NewDestination(baseFolder);
        string parent = Path.GetDirectoryName(baseFolder)!;
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
                snapshot.ArtifactHashes) { PhotoSha256 = result.Dependencies?.Image.Content });
            Verify(staging, key);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) DebugDataStore.DeleteTree(parent, staging); }
    }
    internal static bool Exists(UserSettings settings)
    {
        string key = Key(settings);
        return ManagedVaultDirectory.Find(Folder(key), Owner, key) != null;
    }

    internal static Action PreparePhotoRemoval(PhotoStorageCleanup cleanup, HashSet<string> maskLibraries, UserSettings? selectedMask = null)
    {
        var entries = ReadVaults(out bool referencesKnown);
        var outgoing = new List<(string Folder, VaultManifest Vault)>();
        var keepBlobs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var (folder, vault) = entry;
            bool matchesMask = selectedMask == null;
            string vaultPath = Path.Combine(folder, "vault.json");
            if (vault.PhotoSha256 == null || vault.PhotoSha256 == cleanup.PhotoSha256)
            {
                string runPath = Path.Combine(folder, "run.json");
                var run = PhotoStorageCleanup.Read<DebugDataRun.Manifest>(runPath);
                if (run?.Owner == "SolarShade.CurrentDebugData.v1" && run.Stages != null && run.Inputs != null &&
                    run.Settings != null && AppData.FileKey(runPath) == vault.RunSha256)
                {
                    if (selectedMask != null)
                        matchesMask = run.Settings.SelectedMaskId == selectedMask.SelectedMaskId &&
                            (selectedMask.SelectedMaskId != null || (run.Settings.Model == selectedMask.Model &&
                            run.Settings.Resolution == selectedMask.Resolution && run.Settings.CenteredDisk == selectedMask.CenteredDisk));
                    if (run.Stages.TryGetValue("02-sky-mask", out var mask) && Guid.TryParse(mask.LibraryVersion, out _))
                        maskLibraries.Add(mask.LibraryVersion);
                    if (vault.PhotoSha256 == null && MatchesLegacyPhoto(cleanup.PhotoSha256, run, vault))
                    {
                        // Keep the identity until deletion finishes so an interrupted cleanup can be retried.
                        vault = vault with { PhotoSha256 = cleanup.PhotoSha256 };
                        AppData.Write(vaultPath, vault);
                    }
                }
            }
            if (vault.PhotoSha256 == cleanup.PhotoSha256 && matchesMask) outgoing.Add((folder, vault));
            else keepBlobs.UnionWith(vault.ArtifactHashes.Values);
        }
        return () => RemovePhotoArchives(cleanup, outgoing, keepBlobs, referencesKnown);
    }

    private static List<(string Folder, VaultManifest Vault)> ReadVaults(out bool referencesKnown)
    {
        string root = AppData.PathFor("AcceptedResults");
        var entries = new List<(string, VaultManifest)>();
        referencesKnown = true;
        foreach (string baseFolder in PhotoStorageCleanup.Directories(root))
        {
            string key = Path.GetFileName(baseFolder);
            if (!PhotoStorageCleanup.IsHash(key))
            {
                if (key.EndsWith(".partial", StringComparison.Ordinal)) referencesKnown = false;
                continue;
            }
            foreach (string folder in ManagedVaultDirectory.Locations(baseFolder))
            {
                string vaultPath = Path.Combine(folder, "vault.json");
                var vault = PhotoStorageCleanup.Read<VaultManifest>(vaultPath);
                if (vault?.Owner == Owner && vault.Key == key && vault.ArtifactHashes != null &&
                    vault.ArtifactHashes.Values.All(PhotoStorageCleanup.IsHash)) entries.Add((folder, vault));
                else if (File.Exists(vaultPath) || File.Exists(Path.Combine(folder, "run.json"))) referencesKnown = false;
            }
        }
        return entries;
    }

    private static void RemovePhotoArchives(PhotoStorageCleanup cleanup, List<(string Folder, VaultManifest Vault)> outgoing,
        HashSet<string> keepBlobs, bool referencesKnown)
    {
        if (referencesKnown)
        {
            var discarded = outgoing.SelectMany(entry => entry.Vault.ArtifactHashes.Values).ToHashSet();
            discarded.ExceptWith(keepBlobs);
            foreach (string hash in discarded)
            {
                string blob = BlobPath(hash); DebugDataStore.SafePath(blob);
                if (File.Exists(blob) && AppData.FileKey(blob) == hash) cleanup.RemoveFile(Path.GetDirectoryName(blob)!, hash);
            }
        }
        foreach (var (folder, vault) in outgoing)
        {
            string baseFolder = Folder(vault.Key);
            PvAcceptedVault.DiscardStudy(cleanup, baseFolder, vault.Key);
            cleanup.RemoveFile(folder, "evaluation.json"); cleanup.RemoveFile(folder, "run.json"); cleanup.RemoveFile(folder, "vault.json");
            PhotoStorageCleanup.RemoveIfEmpty(folder);
            if (folder != baseFolder) PhotoStorageCleanup.RemoveIfEmpty(baseFolder);
        }
    }

    private static bool MatchesLegacyPhoto(string photoSha256, DebugDataRun.Manifest run, VaultManifest vault)
    {
        string? Input(string stage, string field) => run.Inputs.TryGetValue(stage, out var input) &&
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        if (Input("sky-mask", "PhotoSha256") is string recorded) return recorded == photoSha256;
        // v0.3.13 recorded composite fingerprints. Match the original pixels without trusting a mutable filename.
        if (run.Settings.SelectedMaskId == null && run.Stages.TryGetValue("02-sky-mask", out var stage) &&
            AppServices.AiMaskKey(run.Settings, photoSha256, stage.LibraryVersion, "mask-orchestration-v1") == Input("sky-mask", "Fingerprint")) return true;
        if (Input("cardinal-directions", "Fingerprint") is not string expected || Input("camera-profile", "Fingerprint") is not string profileKey ||
            !run.Stages.TryGetValue("02-orientation", out var orientation) ||
            !vault.ArtifactHashes.TryGetValue("02-sky-mask/mask-details.json", out string? hash)) return false;
        string file = BlobPath(hash); DebugDataStore.SafePath(file);
        if (!File.Exists(file) || AppData.FileKey(file) != hash) return false;
        var details = PhotoStorageCleanup.Read<MaskGeometryFile>(file)?.Mask;
        if (details?.Disk == null) return false;
        var s = run.Settings;
        return AppData.Key(new { Image = photoSha256, profileKey, details.Width, details.Height, details.Disk,
            s.BottomAzimuth, s.CameraTilt, s.CameraRoll, Library = orientation.LibraryVersion }) == expected;
    }

    private sealed record MaskGeometryFile(SkyMaskResult? Mask);
    internal static (DebugDataRun Run, Evaluation Evaluation)? Restore(DebugDataStore store, UserSettings settings)
    {
        if (WeatherSourceSelection.Problem(settings) != null) return null;
        var inputs = InputDependencies.Capture(settings);
        string key = Key(settings, inputs);
        string? folder = ManagedVaultDirectory.Find(Folder(key), Owner, key);
        if (folder == null) return null;
        var (vault, saved) = Verify(folder, key);
        var manifest = AppData.Read<DebugDataRun.Manifest>(Path.Combine(folder, "run.json"))!;
        if (manifest.Status != "Complete" || manifest.Invalidated != ArtifactGroup.None ||
            manifest.Software != AppServices.SoftwareFingerprint || manifest.Kind != "calculation")
            throw new InvalidDataException("Archived result is not a complete result from this software version.");
        // Keep the inputs that identify this archive stable until its mask and manifest have been restored.
        using var lockedInputs = SnapshotExport.LockInputs(inputs);
        inputs = InputDependencies.Capture(settings);
        if (Key(settings, inputs) != key) return null;
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
                Dependencies = inputs, ManagedDebugRun = store.Current, MaskEdge = manifest.MaskEdge
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
