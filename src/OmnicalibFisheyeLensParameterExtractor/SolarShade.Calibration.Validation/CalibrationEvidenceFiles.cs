using System.Security.Cryptography;
using System.Text.Json;

namespace SolarShade.Calibration.Validation;

/// <summary>Binds a completed calibration's profile formats to the exact review evidence written with them.</summary>
public static class CalibrationEvidenceFiles
{
    public const string FileName = "checkerboard-profile-binding.json";
    private sealed record Binding(int SchemaVersion, Dictionary<string, string> Files);
    public static void Write(string directory, CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != FileName).Order(StringComparer.Ordinal)
            .ToDictionary(p => Path.GetRelativePath(directory, p).Replace('\\', '/'), Hash);
        ct.ThrowIfCancellationRequested();
        SolarShade.Camera.CameraProfileFiles.WriteAtomic(Path.Combine(directory, FileName),
            JsonSerializer.Serialize(new Binding(1, files)), ct);
    }
    public static bool MatchesProfile(string directory, string profilePath)
    {
        var binding = JsonSerializer.Deserialize<Binding>(File.ReadAllText(Path.Combine(directory, FileName)));
        string key = Path.GetExtension(profilePath).Equals(".json", StringComparison.OrdinalIgnoreCase) ? "camera-profile.json" : "calibration.yml";
        if (binding is not { SchemaVersion: 1, Files: not null } ||
            !binding.Files.ContainsKey("checkerboard-detections.json") ||
            !binding.Files.TryGetValue(key, out string? expected) || Hash(profilePath) != expected) return false;
        foreach (var (relative, hash) in binding.Files)
        {
            string file = Path.GetFullPath(Path.Combine(directory, relative));
            string within = Path.GetRelativePath(Path.GetFullPath(directory), file);
            if (Path.IsPathRooted(within) || within == ".." || within.StartsWith(".." + Path.DirectorySeparatorChar) ||
                !File.Exists(file) || Hash(file) != hash) return false;
        }
        return true;
    }
    private static string Hash(string path)
    { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
}
