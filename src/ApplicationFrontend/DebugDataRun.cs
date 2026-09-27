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
        if (status is "Cancelled" or "Failed")
            foreach (string stage in stages.Where(p => p.Value.Status == "Running").Select(p => p.Key).ToArray())
                stages[stage] = stages[stage] with { Status = status };
        AppData.Write(Path.Combine(DirectoryPath, "run.json"), new
        {
            Status = status, StartedUtc = started, UpdatedUtc = DateTimeOffset.UtcNow,
            Settings = settings, Inputs = inputs, Stages = stages, Error = error
        });
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
