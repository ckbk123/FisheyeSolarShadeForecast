namespace SolarShade.Desktop;

/// <summary>Optional result scope sharing the upstream store's lease, but never its mandatory-stage status.
/// Its sidecar manifest deliberately does not mutate the upstream run.json or required-artifact list.</summary>
public sealed class DependentResultStore
{
    private const string Owner = "SolarShade.DependentResult.v1";
    private const string PublishingManifest = ".module-publishing.json";
    private readonly DebugDataStore store;
    private readonly Action<IrradianceSnapshot, Action> withSource;
    private readonly object operation = new();
    private readonly string software;
    private readonly string[] required;
    public string DirectoryPath { get; }
    private string Staging => DirectoryPath + ".pending";
    public sealed record Manifest(string Owner, string SourceId, string SourceFingerprint,
        string SettingsKey, string Software, Dictionary<string, string> Artifacts);

    internal DependentResultStore(DebugDataStore store, string folder, string software, string[] required,
        Action<IrradianceSnapshot, Action> withSource)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(folder, "^[0-9]{2}-[a-z][a-z0-9-]*$") ||
            DebugDataRun.AllStages.Contains(folder)) throw new ArgumentException("A separate optional result folder is required.");
        if (required.Length == 0 || required.Distinct(StringComparer.OrdinalIgnoreCase).Count() != required.Length ||
            required.Any(f => Path.GetFileName(f) != f || f is "module.json" or PublishingManifest or ".owner" || string.IsNullOrWhiteSpace(f)))
            throw new ArgumentException("Unique simple artifact filenames required.");
        this.store = store; this.withSource = withSource; this.software = software; this.required = required.ToArray();
        DirectoryPath = Path.Combine(store.Root, folder);
        lock (store.Gate)
        {
            store.Check();
            // Only an explicitly owned interrupted staging area is recoverable.
            if (Directory.Exists(Staging) && File.Exists(Path.Combine(Staging, ".owner")) &&
                File.ReadAllText(Path.Combine(Staging, ".owner")) == Owner) RemoveOwned(Staging);
        }
    }

    public void Publish(IrradianceSnapshot source, string settingsKey, Action<string> write, CancellationToken ct = default)
    {
        lock (operation)
        {
            ct.ThrowIfCancellationRequested();
            withSource(source, () =>
            {
                if (Directory.Exists(Staging)) RemoveOwned(Staging);
                DebugDataStore.SafePath(Staging);
                Directory.CreateDirectory(Staging); File.WriteAllText(Path.Combine(Staging, ".owner"), Owner);
            });
            try
            {
                write(Staging);
                ct.ThrowIfCancellationRequested();
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in required)
                {
                    string path = Path.Combine(Staging, name); DebugDataStore.SafePath(path);
                    hashes.Add(name, AppData.FileKey(path));
                }
                if (DebugDataStore.Files(Staging).Length != required.Length + 1)
                    throw new IOException("Optional exporter produced unexpected files.");
                AppData.Write(Path.Combine(Staging, "module.json"), new Manifest(Owner, source.Id, source.ContentFingerprint, settingsKey, software, hashes));
                withSource(source, () =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(DirectoryPath)) RemovePublication(DirectoryPath, required);
                    if (!Directory.Exists(DirectoryPath)) Directory.Move(Staging, DirectoryPath);
                    else
                    {
                        // Keep other files in place. Persist the source before moving any output so a partial publish can be retired.
                        if (required.Append("module.json").Any(name => Path.Exists(Path.Combine(DirectoryPath, name))))
                            throw new IOException("An existing file is using an optional-result filename.");
                        string publishing = Path.Combine(DirectoryPath, PublishingManifest);
                        File.Move(Path.Combine(Staging, "module.json"), publishing);
                        foreach (string name in required)
                            File.Move(Path.Combine(Staging, name), Path.Combine(DirectoryPath, name));
                        File.Move(publishing, Path.Combine(DirectoryPath, "module.json"));
                    }
                });
            }
            finally { lock (store.Gate) { if (Directory.Exists(Staging)) RemoveOwned(Staging); } }
        }
    }

    public bool IsCurrent(IrradianceSnapshot source, string settingsKey)
    {
        try { withSource(source, () => Verify(source, settingsKey)); return true; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return false; }
    }

    private Manifest Verify(IrradianceSnapshot source, string settingsKey)
    {
        DebugDataStore.SafePath(DirectoryPath);
        var manifest = AppData.Read<Manifest>(Path.Combine(DirectoryPath, "module.json"));
        if (manifest == null || manifest.Owner != Owner || manifest.SourceId != source.Id ||
            manifest.SourceFingerprint != source.ContentFingerprint || manifest.SettingsKey != settingsKey || manifest.Software != software ||
            manifest.Artifacts == null || !manifest.Artifacts.Keys.Order().SequenceEqual(required.Order()) ||
            File.ReadAllText(Path.Combine(DirectoryPath, ".owner")) != Owner)
            throw new InvalidOperationException("Dependent results are missing or stale. Evaluate again.");
        foreach (var pair in manifest.Artifacts)
        {
            string path = Path.Combine(DirectoryPath, pair.Key); DebugDataStore.SafePath(path);
            if (AppData.FileKey(path) != pair.Value) throw new IOException("Dependent output changed. Evaluate again before exporting.");
        }
        return manifest;
    }

    public void Invalidate()
    {
        lock (store.Gate)
        {
            store.Check();
            if (Directory.Exists(DirectoryPath)) RemovePublication(DirectoryPath, required);
        }
    }

    internal static void DiscardPhotoPublication(DebugDataStore store, string sourceId)
    {
        string path = Path.Combine(store.Root, "07-battery");
        if (Directory.Exists(path))
            RemovePublication(path, ["battery-hourly.csv", "battery-hourly.xlsx", "battery-result.json"], sourceId);
    }

    private static void RemovePublication(string path, string[] files, string? sourceId = null)
    {
        DebugDataStore.SafePath(path);
        if (!Directory.EnumerateFileSystemEntries(path).Any())
        { if (sourceId == null) RemoveOwned(path); return; }
        string marker = Path.Combine(path, ".owner");
        DebugDataStore.SafePath(marker);
        if (!File.Exists(marker) || File.ReadAllText(marker) != Owner)
        {
            if (sourceId != null) return;
            throw new IOException("Unrecognized optional-result folder; it was preserved: " + path);
        }
        string manifestPath = Path.Combine(path, "module.json");
        if (!File.Exists(manifestPath)) manifestPath = Path.Combine(path, PublishingManifest);
        var manifest = PhotoStorageCleanup.Read<Manifest>(manifestPath);
        if (manifest?.Owner != Owner || sourceId != null && sourceId != manifest.SourceId) return;
        foreach (string name in files)
        {
            string file = Path.Combine(path, name);
            DebugDataStore.SafePath(file);
            if (File.Exists(file)) File.Delete(file);
        }
        File.Delete(manifestPath);
        if (Directory.EnumerateFileSystemEntries(path).All(entry => entry == marker))
        {
            File.Delete(marker);
            RemoveOwned(path);
        }
    }

    internal void WithVerifiedPublication(IrradianceSnapshot source, string settingsKey, Action write) =>
        withSource(source, () => { Verify(source, settingsKey); write(); });

    public string Export(IrradianceSnapshot source, string settingsKey, string parent, CancellationToken ct = default)
    {
        parent = Path.GetFullPath(parent);
        if (DebugDataStore.Within(store.Root, parent) || DebugDataStore.Within(parent, store.Root))
            throw new ArgumentException("Choose an export folder outside managed Debug Data.");
        string destination = Path.Combine(parent, "pv-scenario-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        string staging = destination + ".partial";
        try
        {
            withSource(source, () =>
            {
                ct.ThrowIfCancellationRequested(); Verify(source, settingsKey); DebugDataStore.SafePath(staging);
                Directory.CreateDirectory(staging);
                foreach (string name in required.Append("module.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Copy(Path.Combine(DirectoryPath, name), Path.Combine(staging, name));
                }
                var manifest = Verify(source, settingsKey);
                foreach (var pair in manifest.Artifacts)
                    if (AppData.FileKey(Path.Combine(staging, pair.Key)) != pair.Value) throw new IOException("Exported bytes do not match the accepted result.");
                ct.ThrowIfCancellationRequested(); Directory.Move(staging, destination);
            });
            return destination;
        }
        finally { if (Directory.Exists(staging)) DebugDataStore.DeleteTree(parent, staging); }
    }

    private static void RemoveOwned(string path)
    {
        DebugDataStore.SafePath(path);
        bool empty = !Directory.EnumerateFileSystemEntries(path).Any();
        if (!empty && (!File.Exists(Path.Combine(path, ".owner")) || File.ReadAllText(Path.Combine(path, ".owner")) != Owner))
            throw new IOException("Unrecognized optional-result folder; it was preserved: " + path);
        var attributes = File.GetAttributes(path);
        bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        try
        {
            // A read-only directory can survive cleanup after its ownership marker is removed.
            // Change only this verified empty/owned root, never unknown files or child folders.
            if (readOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            // Nonrecursive deletion also preserves any entry that arrives after the empty check.
            if (empty) Directory.Delete(path, recursive: false);
            else DebugDataStore.DeleteTree(Path.GetDirectoryName(path)!, path);
        }
        catch
        {
            if (readOnly && Directory.Exists(path))
                try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
