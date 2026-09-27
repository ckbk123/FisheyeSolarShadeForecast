namespace SolarShade.Desktop;

/// <summary>Run bookkeeping only. Scientific artifacts are produced by their owning libraries.</summary>
public sealed class DebugDataRun
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("SOLARSHADE_DEBUG_DIR") ?? Path.Combine(AppContext.BaseDirectory, "Debug Data");
    public string DirectoryPath { get; }
    private readonly UserSettings settings;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, StageRecord> stages = new();
    private readonly Dictionary<string, object> inputs = new();
    private readonly Dictionary<string, string> artifactHashes = new();
    internal object SyncRoot { get; } = new();
    private ArtifactGroup invalidated;
    private bool cleanupPending;
    private string currentStatus = "Running";
    private string? manifestHash;
    public bool IsCurrent { get { lock (SyncRoot) return currentStatus == "Complete" && invalidated == ArtifactGroup.None; } }
    private sealed record StageRecord(string Status, string Origin, string LibraryVersion, string[] Artifacts);

    public DebugDataRun(UserSettings settings, string kind = "calculation", string? root = null)
    {
        this.settings = settings with { };
        DirectoryPath = Path.Combine(root ?? DefaultRoot, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        foreach (string stage in kind == "calibration" ? new[] { "01-calibration" } : kind == "orientation" ? new[] { "01-calibration", "02-sky-mask", "02-orientation" } : new[] { "01-calibration", "02-sky-mask", "02-orientation", "03-irradiance", "04-solar-positions", "05-transposition", "06-shading" })
            stages[stage] = new("Pending", "", "", []);
        Status("Running");
    }

    public string BeginStage(string name, string origin, Type owner)
    {
        string path = Path.Combine(DirectoryPath, name);
        Directory.CreateDirectory(path);
        stages[name] = new("Running", origin, AppData.LibraryVersion(owner), []);
        Status("Running");
        return path;
    }

    public void CompleteStage(string name, IReadOnlyList<string> artifacts, bool skipped = false)
    {
        var relative = artifacts.Select(path =>
        {
            string full = Path.GetFullPath(path);
            string local = Path.GetRelativePath(DirectoryPath, full);
            if (Path.IsPathRooted(local) || local == ".." || local.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new IOException("A stage artifact is outside its Debug Data run.");
            if (!File.Exists(full)) throw new IOException("A stage reported a missing artifact: " + full);
            artifactHashes[local.Replace('\\', '/')] = AppData.FileKey(full);
            return local.Replace('\\', '/');
        }).ToArray();
        stages[name] = stages[name] with { Status = skipped ? "Skipped" : "Complete", Artifacts = relative };
        Status("Running");
    }

    public void RecordInput(string name, object fingerprint) { inputs[name] = fingerprint; Status("Running"); }
    public void Origin(string stage, string origin) { stages[stage] = stages[stage] with { Origin = origin }; Status("Running"); }

    public void Skip(string name, string reason)
    {
        stages[name] = new("Skipped", reason, "", []);
        Status("Running");
    }

    public void Complete()
    {
        if (stages.Values.Any(stage => stage.Status is "Running" or "Pending")) throw new InvalidOperationException("A stage has not completed its debug output.");
        Status("Complete");
    }

    public void Status(string status, string? error = null)
    {
        currentStatus = invalidated == ArtifactGroup.None ? status : "Stale";
        if (status is "Cancelled" or "Failed")
            foreach (string stage in stages.Where(p => p.Value.Status == "Running").Select(p => p.Key).ToArray())
                stages[stage] = stages[stage] with { Status = status };
        AppData.Write(Path.Combine(DirectoryPath, "run.json"), new
        {
            Status = currentStatus, StartedUtc = started, UpdatedUtc = DateTimeOffset.UtcNow,
            Settings = settings, Inputs = inputs, Stages = stages, ArtifactHashes = artifactHashes,
            InvalidatedGroups = InputDependencies.Groups.Where(g => invalidated.HasFlag(g)).Select(g => g.ToString()).ToArray(), Error = error
        });
        manifestHash = AppData.FileKey(Path.Combine(DirectoryPath, "run.json"));
    }

    public ArtifactGroup ChangedArtifacts(bool checkContents)
    {
        lock (SyncRoot)
        {
            var changed = ArtifactGroup.None;
            string manifest = Path.Combine(DirectoryPath, "run.json");
            try
            {
                if (!File.Exists(manifest) || (checkContents && AppData.FileKey(manifest) != manifestHash)) return ArtifactGroup.All;
            }
            catch (IOException) { return ArtifactGroup.All; }
            catch (UnauthorizedAccessException) { return ArtifactGroup.All; }
            foreach (var (relative, hash) in artifactHashes)
            {
                var group = InputDependencies.ForArtifact(relative);
                if ((invalidated & group) != 0) continue;
                string path = Path.Combine(DirectoryPath, relative);
                try { if (!File.Exists(path) || (checkContents && AppData.FileKey(path) != hash)) changed |= group; }
                catch (IOException) { changed |= group; }
                catch (UnauthorizedAccessException) { changed |= group; }
            }
            return changed;
        }
    }

    /// <summary>Called only after this run's writer has relinquished ownership.</summary>
    public void Invalidate(ArtifactGroup groups, IEnumerable<string> protectedInputs)
    {
        lock (SyncRoot)
        {
            if ((groups & ~invalidated) == 0 && !cleanupPending) return;
            invalidated |= groups; cleanupPending = true;
            foreach (string stage in stages.Keys.ToArray())
                if (artifactHashes.Keys.Any(p => p.StartsWith(stage + "/", StringComparison.Ordinal) && (InputDependencies.ForArtifact(p) & invalidated) != 0))
                    stages[stage] = stages[stage] with { Status = "Stale" };
            // Advertise stale before attempting deletion, including when Excel holds a file open.
            Status("Stale", "Inputs or artifacts changed; an explicit update is required.");
            string root = Path.GetFullPath(DirectoryPath);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Cannot clean a redirected debug directory.");
            var protectedPaths = protectedInputs.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (string candidate in Directory.EnumerateFiles(root, "*", options))
            {
                string full = Path.GetFullPath(candidate), relative = Path.GetRelativePath(root, full);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new IOException("Invalid debug artifact path.");
                if ((InputDependencies.ForArtifact(relative) & invalidated) == 0) continue;
                if (protectedPaths.Contains(full)) throw new IOException("A selected input is inside stale debug output. Save it outside that run before updating: " + full);
                File.Delete(full);
            }
            cleanupPending = false;
        }
    }

    public void CopyCurrent(string destination)
    {
        lock (SyncRoot)
        {
            if (!IsCurrent || ChangedArtifacts(checkContents: true) != ArtifactGroup.None)
                throw new InvalidOperationException("Debug data is stale or incomplete. Update results before exporting.");
            CopyCompleted(DirectoryPath, destination);
        }
    }

    public static void CopyCompleted(string source, string destination)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        string relative = Path.GetRelativePath(source, destination);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new ArgumentException("Choose an export destination outside the source Debug Data run.");
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "run.json")));
        if (manifest.RootElement.GetProperty("Status").GetString() != "Complete")
            throw new InvalidOperationException("Only a completed run can be exported.");
        Directory.CreateDirectory(destination);
        // Publish the completed manifest last; a failed copy cannot advertise success.
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(p => p != Path.Combine(source, "run.json")))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, false);
        }
        File.Copy(Path.Combine(source, "run.json"), Path.Combine(destination, "run.json"), false);
    }
}
