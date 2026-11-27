namespace SolarShade.Desktop;

/// <summary>Remove registered artifacts of one photograph and keep source files or unrelated directory contents.</summary>
internal sealed class PhotoStorageCleanup(SourceIdentity photo, IEnumerable<string> inputs)
{
    private readonly HashSet<string> protectedFiles = inputs.Append(photo.Path).Where(p => p.Length != 0)
        .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public string PhotoSha256 => photo.Content;
    public static bool IsHash(string? name) => name is { Length: 64 } && name.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static IEnumerable<string> Directories(string root)
    {
        DebugDataStore.SafePath(root);
        return Directory.Exists(root) ? Directory.GetDirectories(root).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0) : [];
    }

    public static T? Read<T>(string path) where T : class
    {
        DebugDataStore.SafePath(path);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(stream); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public void RemoveFile(string directory, string name)
    {
        if (Path.GetFileName(name) != name) throw new IOException("Invalid managed filename.");
        string path = Path.GetFullPath(Path.Combine(directory, name));
        if (protectedFiles.Contains(path)) return;
        DebugDataStore.SafePath(path);
        if (File.Exists(path)) File.Delete(path);
    }

    public static void RemoveIfEmpty(string directory)
    {
        DebugDataStore.SafePath(directory);
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
