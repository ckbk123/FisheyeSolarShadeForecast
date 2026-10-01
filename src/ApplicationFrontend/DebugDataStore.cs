using System.Text.Json;
using System.Text.RegularExpressions;

namespace SolarShade.Desktop;

/// <summary>Exclusive process ownership of one published dataset and one bounded staging area.</summary>
public sealed class DebugDataStore : IDisposable
{
    public string Root { get; }
    public string Staging { get; }
    public object Gate { get; } = new();
    internal DebugDataRun? Current { get; set; }
    private readonly FileStream lease;
    private bool disposed;
    private bool recovered;
    private readonly HashSet<string> deferredCleanup = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyCollection<string> DeferredCleanup => deferredCleanup;
    public DebugDataStore(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetDirectoryName(Root) == null) throw new IOException("Choose a dedicated debug folder.");
        SafePath(Root); Staging = Root + ".staging"; SafePath(Staging); SafePath(Root + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        try { lease = new FileStream(Root + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("This debug dataset is already in use. Close the other SolarShade instance and retry.", ex); }
        try
        {
            if (File.Exists(Path.Combine(Root, "run.json"))) Current = DebugDataRun.Restore(this);
            else if (DebugDataRun.AllStages.Any(name => Directory.Exists(Path.Combine(Root, name))))
                throw new IOException("Existing stage folders have no recognized manifest. Choose a separate debug folder to avoid overwriting them.");
            if (Directory.Exists(Staging))
            {
                if (!File.Exists(Path.Combine(Staging, ".solarshade-staging")) || File.ReadAllText(Path.Combine(Staging, ".solarshade-staging")) != "SolarShade current dataset staging v1") throw new IOException("Unrecognized staging folder: " + Staging);
                DeleteTree(Staging, Staging);
            }
        }
        catch { lease.Dispose(); throw; }
    }
    internal void Check() { ObjectDisposedException.ThrowIf(disposed, this); SafePath(Root); }
    internal void StartStaging()
    {
        Check(); Directory.CreateDirectory(Staging);
        File.WriteAllText(Path.Combine(Staging, ".solarshade-staging"), "SolarShade current dataset staging v1");
    }
    internal void ClearStaging() { if (Directory.Exists(Staging)) DeleteTree(Staging, Staging); }
    public UserSettings PrepareInputs(UserSettings settings)
    {
        lock (Gate)
        {
            Check();
            string Protect(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return path;
                string full = PortablePaths.Resolve(path);
                // Any selected input under managed storage is protected, including a newly
                // selected historical/unknown folder. Never enumerate history while editing.
                if (!Within(Root, full)) return path;
                if (Within(Root, AppData.Root)) throw new IOException("Data must be outside Debug Data to preserve selected inputs.");
                if (!File.Exists(full) && !Directory.Exists(full)) throw new IOException("A selected input in old debug data is missing: " + full);
                return PortablePaths.Store(Preserve(full, "Inputs"));
            }
            var saved = PortablePaths.Map(settings, Protect);
            // Save references first, so interruption cannot strand selected inputs.
            if (saved != settings) AppData.SaveSettings(saved);
            if (!recovered) { Current?.Recover(InputDependencies.Capture(saved).SourcePaths); recovered = true; }
            return saved;
        }
    }
    /// <summary>Explicit maintenance, never called by startup, edits or calculations.</summary>
    public UserSettings CleanHistory(UserSettings settings, CancellationToken ct = default)
    {
        var saved = PrepareInputs(settings);
        var candidates = Directory.Exists(Root) ? Directory.GetDirectories(Root) : [];
        foreach (string old in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // Discovery is outside the storage lock; revalidate ownership before deleting.
            if (!IsLegacyRun(old)) continue;
            lock (Gate)
            {
                Check();
                if (!IsLegacyRun(old)) continue;
                try
                {
                    // Old diagnostics are optional housekeeping. Preserve read-only trees intact,
                    // including OneDrive folders, rather than partly deleting them before failing.
                    var entries = Files(old).Concat(Directory.GetDirectories(old, "*", SearchOption.AllDirectories)).Append(old);
                    if (entries.Any(path => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0))
                        throw new IOException("Old diagnostics contain read-only files or folders.");
                    DeleteTree(Root, old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    bool first = deferredCleanup.Add(old);
                    try { if (first) File.AppendAllText(AppData.PathFor("maintenance.log"), $"{DateTimeOffset.Now:O} Deferred old diagnostic cleanup: {old}. {ex.Message}{Environment.NewLine}"); }
                    catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return saved;
    }
    public static string Preserve(string source, string category)
    {
        source = Path.GetFullPath(source); SafePath(source);
        bool folder = Directory.Exists(source);
        var files = folder ? Files(source) : new[] { source };
        string key = AppData.Key(files.Select(f => new { Name = folder ? Path.GetRelativePath(source, f) : Path.GetFileName(f), Hash = AppData.FileKey(f) }).ToArray());
        string directory = Path.Combine(AppData.Root, category, key); SafePath(directory); Directory.CreateDirectory(directory);
        foreach (string file in files)
        {
            string target = Path.Combine(directory, folder ? Path.GetRelativePath(source, file) : Path.GetFileName(file));
            SafePath(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            { if (AppData.FileKey(target) != AppData.FileKey(file)) throw new IOException("A saved input copy has changed: " + target); }
            else File.Copy(file, target, false);
        }
        return folder ? directory : Path.Combine(directory, Path.GetFileName(source));
    }
    private static bool IsLegacyRun(string path)
    {
        if (!Regex.IsMatch(Path.GetFileName(path), @"^(calculation|orientation|calibration)-\d{8}-\d{6}-\d{3}-[a-fA-F0-9]{32}$")) return false;
        try
        {
            SafePath(path);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "run.json")));
            var root = document.RootElement;
            if (!root.TryGetProperty("Settings", out _) || !root.TryGetProperty("StartedUtc", out _) || !root.TryGetProperty("Inputs", out _)) return false;
            var stages = root.GetProperty("Stages").EnumerateObject().ToArray();
            if (stages.Length == 0 || stages.Any(s => DebugDataRun.StageGroups(s.Name) == ArtifactGroup.None)) return false;
            var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(path, "run.json") };
            foreach (var stage in stages)
            {
                string? version = stage.Value.GetProperty("LibraryVersion").GetString();
                if (version?.Length > 0 && !Guid.TryParse(version, out _)) return false;
                foreach (var artifact in stage.Value.GetProperty("Artifacts").EnumerateArray())
                {
                    string full = Path.GetFullPath(Path.Combine(path, artifact.GetString()!));
                    if (!Within(Path.Combine(path, stage.Name), full)) return false;
                    registered.Add(full);
                }
            }
            return Files(path).All(registered.Contains);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }
    internal static bool Within(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar);
    }
    internal static void SafePath(string path, HashSet<string>? checkedPaths = null)
    {
        for (string? cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
        {
            if (checkedPaths != null && !checkedPaths.Add(cursor)) return;
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Debug storage cannot follow a redirected path: " + cursor);
        }
    }
    internal static string[] Files(string directory)
    {
        SafePath(directory);
        var files = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            SafePath(entry);
            if (Directory.Exists(entry)) files.AddRange(Files(entry)); else files.Add(entry);
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static void DeleteTree(string allowedRoot, string target)
    {
        target = Path.GetFullPath(target);
        if (!Within(allowedRoot, target)) throw new IOException("Cleanup target is outside managed storage.");
        SafePath(target);
        foreach (string file in Files(target)) File.Delete(file);
        foreach (string directory in Directory.GetDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length)) Directory.Delete(directory);
        Directory.Delete(target);
    }
    public void Dispose() { lock (Gate) { if (disposed) return; disposed = true; lease.Dispose(); } }
}
