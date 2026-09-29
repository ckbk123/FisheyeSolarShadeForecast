namespace SolarShade.Desktop;

/// <summary>Optional result scope sharing the upstream store's lease, but never its mandatory-stage status.
/// Its sidecar manifest deliberately does not mutate the upstream run.json or required-artifact list.</summary>
public sealed class DependentResultStore
{
    private const string Owner = "SolarShade.DependentResult.v1";
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
            required.Any(f => Path.GetFileName(f) != f || f is "module.json" or ".owner" || string.IsNullOrWhiteSpace(f)))
            throw new ArgumentException("Unique simple artifact filenames required.");
        this.store = store; this.withSource = withSource; this.software = software; this.required = required.ToArray();
        DirectoryPath = Path.Combine(store.Root, folder);
        lock (store.Gate)
        {
            store.Check();
            // Only an explicitly owned interrupted staging area is recoverable.
            if (Directory.Exists(Staging)) RemoveOwned(Staging);
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
                    if (Directory.Exists(DirectoryPath)) RemoveOwned(DirectoryPath);
                    Directory.Move(Staging, DirectoryPath);
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
            if (Directory.Exists(DirectoryPath)) RemoveOwned(DirectoryPath);
        }
    }

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
        if (!File.Exists(Path.Combine(path, ".owner")) || File.ReadAllText(Path.Combine(path, ".owner")) != Owner)
            throw new IOException("Unrecognized optional-result folder; it was preserved: " + path);
        DebugDataStore.DeleteTree(Path.GetDirectoryName(path)!, path);
    }
}
