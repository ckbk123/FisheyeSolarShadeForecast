using System.IO.Compression;
using System.Text.Json;

namespace SolarShade.Desktop;

/// <summary>One recoverable value per stage, separate from published diagnostics.</summary>
internal sealed class CurrentValueCache(string debugRoot)
{
    private sealed record Entry<T>(string Key, string Software, T Value);
    private string PathFor(string stage) => AppData.PathFor(Path.Combine("current-values", AppData.Key(Path.GetFullPath(debugRoot).ToUpperInvariant()), stage + ".json.gz"));
    public T? Read<T>(string stage, string key) where T : class
    {
        try
        {
            string path = PathFor(stage);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var entry = JsonSerializer.Deserialize<Entry<T>>(gzip);
            return entry?.Key == key && entry.Software == AppServices.SoftwareFingerprint ? entry.Value : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public void Write<T>(string stage, string key, T value)
    {
        string path = PathFor(stage), temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var file = File.Create(temp)) using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, new Entry<T>(key, AppServices.SoftwareFingerprint, value));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
