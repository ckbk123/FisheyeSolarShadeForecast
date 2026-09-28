using System.Text.Json;

namespace SolarShade.Desktop;

/// <summary>Publishes library artifacts at stable paths; scientific file schemas remain library-owned.</summary>
public sealed class DebugDataRun : IDisposable
{
    public static string DefaultRoot => Environment.GetEnvironmentVariable("SOLARSHADE_DEBUG_DIR") ?? Path.Combine(AppContext.BaseDirectory, "Debug Data");
    private readonly DebugDataStore store;
    private readonly bool ownsStore;
    private Manifest state = new();
    private string? manifestHash;
    private bool cleanupPending;
    public string DirectoryPath => store.Root;
    public string Id => state.Id;
    internal object SyncRoot => store.Gate;
    internal ArtifactGroup Invalidated => state.Invalidated;
    public bool IsCurrent { get { lock (SyncRoot) return store.Current == this && state.Status == "Complete" && state.Invalidated == ArtifactGroup.None; } }
    public sealed record StageRecord(string Status, string Origin, string LibraryVersion, string[] Artifacts);
    public sealed class Manifest
    {
        public string Owner { get; set; } = "SolarShade.CurrentDebugData.v1";
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; } = "calculation";
        public int UpdateNumber { get; set; }
        public string Software { get; set; } = AppServices.SoftwareFingerprint;
        public string Status { get; set; } = "Running";
        public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedUtc { get; set; }
        public DateTimeOffset? LastSuccessfulUpdateUtc { get; set; }
        public UserSettings Settings { get; set; } = new();
        public Dictionary<string, JsonElement> Inputs { get; set; } = new();
        public Dictionary<string, StageRecord> Stages { get; set; } = new();
        public Dictionary<string, ArtifactGroup> PendingGroups { get; set; } = new();
        public Dictionary<string, string> ArtifactHashes { get; set; } = new();
        public Dictionary<ArtifactGroup, string> Keys { get; set; } = new();
        public ArtifactGroup Invalidated { get; set; } = ArtifactGroup.All;
        public string[] InvalidatedGroups => InputDependencies.Groups.Where(g => Invalidated.HasFlag(g)).Select(g => g.ToString()).ToArray();
        public string? Error { get; set; }
    }
    public DebugDataRun(UserSettings settings, string kind = "calculation", string? root = null)
        : this(new DebugDataStore(root ?? DefaultRoot), settings, kind, true) { }
    internal DebugDataRun(DebugDataStore store, UserSettings settings, string kind = "calculation", bool ownsStore = false)
    {
        this.store = store; this.ownsStore = ownsStore;
        try
        {
        lock (SyncRoot)
        {
            store.Check();
            settings = store.PrepareInputs(settings);
            var inputs = InputDependencies.Capture(settings);
            var previous = store.Current;
            previous?.Invalidate(previous.Differences(inputs) | InputDependencies.WithDependents(previous.ChangedArtifacts(true)), inputs.SourcePaths);
            if (kind == "calculation" && previous?.state.Kind == "calibration")
                previous.Invalidate(ArtifactGroup.Profile | InputDependencies.PoseChain, inputs.SourcePaths);
            previous?.Finish();
            state = previous == null ? new() : JsonSerializer.Deserialize<Manifest>(JsonSerializer.Serialize(previous.state))!;
            state.Id = Guid.NewGuid().ToString("N"); state.StartedUtc = DateTimeOffset.UtcNow; state.Kind = kind; state.UpdateNumber++;
            state.Settings = settings with { }; state.Software = AppServices.SoftwareFingerprint; state.Keys = inputs.Keys.ToDictionary();
            string[] required = kind == "calibration" ? ["01-calibration"] : kind == "orientation" ? ["01-calibration", "02-sky-mask", "02-orientation"] : AllStages;
            foreach (string name in required) state.Stages.TryAdd(name, new("Pending", "", "", []));
            store.Current = this; store.StartStaging(); Status("Running");
        }
        }
        catch { if (ownsStore) store.Dispose(); throw; }
    }
    private DebugDataRun(DebugDataStore store, Manifest state) { this.store = store; this.state = state; manifestHash = AppData.FileKey(Path.Combine(DirectoryPath, "run.json")); }
    internal static DebugDataRun Restore(DebugDataStore store)
    {
        var state = AppData.Read<Manifest>(Path.Combine(store.Root, "run.json"));
        if (state?.Owner != "SolarShade.CurrentDebugData.v1") throw new IOException("This debug folder has an unrecognized manifest. Choose a separate debug folder.");
        foreach (string file in state.ArtifactHashes.Keys)
            if (!DebugDataStore.Within(store.Root, Path.Combine(store.Root, file)) || InputDependencies.ForArtifact(file) == ArtifactGroup.None)
                throw new IOException("Invalid path in debug manifest.");
        return new(store, state);
    }
    internal static readonly string[] AllStages = ["01-calibration", "02-sky-mask", "02-orientation", "03-irradiance", "04-solar-positions", "05-transposition", "06-shading"];
    internal static ArtifactGroup StageGroups(string name) => name == "04-solar-positions" ? ArtifactGroup.Solar | ArtifactGroup.SunPath : InputDependencies.ForArtifact(name + "/artifact");
    internal ArtifactGroup Differences(InputDependencies next) => state.Software != AppServices.SoftwareFingerprint ? ArtifactGroup.All :
        InputDependencies.Groups.Where(g => !state.Keys.TryGetValue(g, out var key) || key != next.Keys[g]).Aggregate(ArtifactGroup.None, (a, g) => a | g);
    private void Check() { store.Check(); if (store.Current != this) throw new InvalidOperationException("This result has been replaced by a newer update."); }
    public bool NeedsWrite(ArtifactGroup group) { lock (SyncRoot) { Check(); return (state.Invalidated & group) != 0; } }
    public string BeginStage(string name, string origin, Type owner)
    {
        lock (SyncRoot)
        {
            Check(); if (StageGroups(name) == ArtifactGroup.None) throw new ArgumentException("Unknown debug stage.");
            string path = Path.Combine(store.Staging, name); Directory.CreateDirectory(path);
            var old = state.Stages.GetValueOrDefault(name);
            state.Stages[name] = new("Running", NeedsWrite(StageGroups(name)) ? origin : "Reused published artifacts", AppData.LibraryVersion(owner), old?.Artifacts ?? []);
            state.PendingGroups[name] = state.Invalidated & StageGroups(name);
            Status("Running"); return path;
        }
    }
    public void CompleteStage(string name, IReadOnlyList<string> artifacts, bool skipped = false)
    {
        lock (SyncRoot)
        {
            Check(); string staging = Path.Combine(store.Staging, name);
            var paths = artifacts.Select(Path.GetFullPath).ToArray();
            foreach (string file in paths)
            {
                DebugDataStore.SafePath(file);
                if (!DebugDataStore.Within(staging, file) || !File.Exists(file)) throw new IOException("A stage reported a missing or external artifact: " + file);
            }
            var published = state.ArtifactHashes.Keys.Where(p => p.StartsWith(name + "/") && File.Exists(Path.Combine(DirectoryPath, p))).ToHashSet();
            foreach (string file in paths)
            {
                string relative = Path.GetRelativePath(store.Staging, file).Replace('\\', '/');
                string target = Path.Combine(DirectoryPath, relative); DebugDataStore.SafePath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, true); state.ArtifactHashes[relative] = AppData.FileKey(target); published.Add(relative);
            }
            state.Invalidated &= ~StageGroups(name);
            state.PendingGroups.Remove(name);
            state.Stages[name] = state.Stages[name] with { Status = skipped ? "Skipped" : "Complete", Artifacts = published.Order().ToArray() };
            Status("Running");
        }
    }
    public void RecordInput(string name, object fingerprint) { lock (SyncRoot) { Check(); state.Inputs[name] = JsonSerializer.SerializeToElement(fingerprint); Status("Running"); } }
    public void Origin(string stage, string origin) { lock (SyncRoot) { Check(); state.Stages[stage] = state.Stages[stage] with { Origin = origin }; Status("Running"); } }
    public void Skip(string name, string reason)
    {
        lock (SyncRoot)
        {
            Check(); Invalidate(StageGroups(name), InputDependencies.Capture(state.Settings).SourcePaths);
            state.Invalidated &= ~StageGroups(name); state.Stages[name] = new("Skipped", reason, "", []); Status("Running");
        }
    }
    public void Complete()
    {
        lock (SyncRoot)
        {
            Check();
            if (state.Kind != "calculation") { Status("Partial"); return; }
            if (state.Invalidated != ArtifactGroup.None || state.Stages.Values.Any(stage => stage.Status is not ("Complete" or "Skipped"))) throw new InvalidOperationException("A stage has not completed its debug output.");
            state.LastSuccessfulUpdateUtc = DateTimeOffset.UtcNow; Status("Complete");
        }
    }
    internal void SetCalibrationProfile(string path)
    {
        lock (SyncRoot)
        {
            Check(); state.Settings = state.Settings with { ProfilePath = PortablePaths.Store(path), CoverageAngle = 0 };
            state.Keys = InputDependencies.Capture(state.Settings).Keys.ToDictionary();
        }
    }
    public void Status(string status, string? error = null)
    {
        lock (SyncRoot)
        {
            Check(); state.Status = status; state.Error = error; state.UpdatedUtc = DateTimeOffset.UtcNow;
            if (status is "Cancelled" or "Failed" or "Interrupted")
                foreach (string name in state.Stages.Where(p => p.Value.Status == "Running").Select(p => p.Key).ToArray())
                { state.Invalidated |= state.PendingGroups.GetValueOrDefault(name, StageGroups(name)); state.Stages[name] = state.Stages[name] with { Status = status }; }
            AppData.Write(Path.Combine(DirectoryPath, "run.json"), state);
            manifestHash = AppData.FileKey(Path.Combine(DirectoryPath, "run.json"));
        }
    }
    public ArtifactGroup ChangedArtifacts(bool checkContents)
    {
        lock (SyncRoot)
        {
            Check(); var changed = ArtifactGroup.None;
            try
            {
                string manifest = Path.Combine(DirectoryPath, "run.json");
                if (!File.Exists(manifest) || (checkContents && AppData.FileKey(manifest) != manifestHash)) return ArtifactGroup.All;
                foreach (var (relative, hash) in state.ArtifactHashes)
                {
                    var group = InputDependencies.ForArtifact(relative); if ((state.Invalidated & group) != 0) continue;
                    string path = Path.Combine(DirectoryPath, relative); DebugDataStore.SafePath(path);
                    if (!File.Exists(path) || (checkContents && AppData.FileKey(path) != hash)) changed |= group;
                }
            }
            catch (IOException) { return ArtifactGroup.All; }
            catch (UnauthorizedAccessException) { return ArtifactGroup.All; }
            return changed;
        }
    }
    public void Invalidate(ArtifactGroup groups, IEnumerable<string> protectedInputs)
    {
        lock (SyncRoot)
        {
            Check(); if ((groups & ~state.Invalidated) == 0 && !cleanupPending) return;
            state.Invalidated |= groups; cleanupPending = true;
            foreach (string stage in state.Stages.Keys.ToArray())
                if ((StageGroups(stage) & groups) != 0) state.Stages[stage] = state.Stages[stage] with { Status = "Stale" };
            Status("Stale", "Inputs or artifacts changed; an explicit update is required.");
            RemoveInvalid(protectedInputs); cleanupPending = false;
        }
    }
    private void RemoveInvalid(IEnumerable<string> protectedInputs)
    {
        var protectedPaths = protectedInputs.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string stage in AllStages)
        {
            string directory = Path.Combine(DirectoryPath, stage); if (!Directory.Exists(directory)) continue;
            foreach (string candidate in DebugDataStore.Files(directory))
            {
                string relative = Path.GetRelativePath(DirectoryPath, candidate).Replace('\\', '/');
                if ((InputDependencies.ForArtifact(relative) & state.Invalidated) == 0) continue;
                if (protectedPaths.Contains(candidate)) throw new IOException("A selected input must be saved outside debug output before cleanup: " + candidate);
                File.Delete(candidate); state.ArtifactHashes.Remove(relative);
            }
        }
    }
    internal void Finish()
    {
        lock (SyncRoot)
        {
            Check();
            try { RemoveInvalid(InputDependencies.Capture(state.Settings).SourcePaths); store.ClearStaging(); }
            catch (Exception ex) { Status("Failed", "Debug cleanup failed: " + ex.Message); throw; }
        }
    }
    internal void Recover(IEnumerable<string> protectedInputs)
    {
        lock (SyncRoot)
        {
            if (state.Status == "Running") Status("Interrupted", "The previous update was interrupted. Click Update results to recover.");
            if (state.Invalidated != ArtifactGroup.None) RemoveInvalid(protectedInputs);
        }
    }
    public void CopyCurrent(string destination)
    {
        lock (SyncRoot)
        {
            Check();
            if (!IsCurrent || ChangedArtifacts(true) != ArtifactGroup.None) throw new InvalidOperationException("Debug data is stale or incomplete. Update results before exporting.");
            CopyFiles(DirectoryPath, destination);
        }
    }
    public static void CopyCompleted(string source, string destination)
    {
        if (DebugDataStore.Within(source, destination)) throw new ArgumentException("Choose an export destination outside Debug Data.");
        using var storage = new DebugDataStore(source);
        if (storage.Current == null) throw new InvalidOperationException("No current dataset exists.");
        storage.Current.CopyCurrent(destination);
    }
    private static void CopyFiles(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (DebugDataStore.Within(source, destination)) throw new ArgumentException("Choose an export destination outside Debug Data.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "run.json")));
        if (manifest.RootElement.GetProperty("Status").GetString() != "Complete") throw new InvalidOperationException("Only a completed calculation can be exported.");
        Directory.CreateDirectory(destination);
        foreach (var entry in manifest.RootElement.GetProperty("ArtifactHashes").EnumerateObject())
        {
            string file = Path.Combine(source, entry.Name); DebugDataStore.SafePath(file);
            if (!DebugDataStore.Within(source, file)) throw new IOException("Invalid export source path.");
            string target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false);
        }
        File.Copy(Path.Combine(source, "run.json"), Path.Combine(destination, "run.json"), false);
    }
    public void Dispose() { if (ownsStore) { try { Finish(); } finally { store.Dispose(); } } }
}
