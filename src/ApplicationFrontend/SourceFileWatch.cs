namespace SolarShade.Desktop;

/// <summary>Notifications request validation only, never scientific work.</summary>
public sealed class SourceFileWatch(Action changed) : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private string identity = "";
    public void SetFiles(IEnumerable<string> paths)
    {
        var files = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string next = string.Join("\n", files);
        if (next == identity) return;
        Dispose(); identity = next;
        foreach (string file in files)
        {
            try
            {
                string full = Path.GetFullPath(PortablePaths.Resolve(file));
                if (!Directory.Exists(Path.GetDirectoryName(full))) continue;
                var watcher = new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full))
                { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime };
                watcher.Changed += (_, _) => changed(); watcher.Created += (_, _) => changed();
                watcher.Deleted += (_, _) => changed(); watcher.Renamed += (_, _) => changed();
                watcher.Error += (_, _) => changed();
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }
    public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); identity = ""; }
}
