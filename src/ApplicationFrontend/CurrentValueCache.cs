using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SolarShade.Irradiance;

namespace SolarShade.Desktop;

/// <summary>One recoverable value per stage, separate from published diagnostics.</summary>
internal sealed class CurrentValueCache(string debugRoot)
{
    private const uint Magic = 0x32435653;
    private sealed record Header(string Key, string Software, string PayloadSha256);
    private sealed record SolarRow(SolarGeometrySample AtLabel, IReadOnlyList<SolarGeometrySample> Source, IReadOnlyList<SolarGeometrySample>? Selected);
    private sealed record CompactSolar(SolarShade.Irradiance.IrradianceDataset Dataset, SolarRow[] Rows, string Convention)
    {
        public static CompactSolar From(SolarTimeline value) => new(value.Dataset, value.Intervals.Select(r => new SolarRow(r.AtLabel,
            r.SourceSamples, r.SourceSamples.SequenceEqual(r.Samples) ? null : r.Samples)).ToArray(), value.Convention);
        public SolarTimeline Expand()
        {
            if (Rows.Length != Dataset.Intervals.Count) throw new JsonException("Invalid solar cache interval count.");
            return new(Dataset, Rows.Select((r, i) => new SolarIntervalGeometry(Dataset.Intervals[i], r.AtLabel, r.Source, r.Selected ?? r.Source)).ToArray(), Convention);
        }
    }
    internal string PathFor(string stage) => AppData.PathFor(Path.Combine("current-values-v2", AppData.Key(Path.GetFullPath(debugRoot).ToUpperInvariant()), stage + ".cache"));
    public T? Read<T>(string stage, string key) where T : class
    {
        using var timing = PerformanceTrace.Phase("cache-read", stage);
        try
        {
            string path = PathFor(stage);
            if (!File.Exists(path)) { timing.Outcome = "missing"; return null; }
            using var file = File.OpenRead(path); using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, true);
            if (reader.ReadUInt32() != Magic) return null;
            int length = reader.ReadInt32();
            if (length is < 1 or > 4096) return null;
            byte[] headerBytes = reader.ReadBytes(length);
            if (headerBytes.Length != length) return null;
            var header = JsonSerializer.Deserialize<Header>(headerBytes);
            if (header?.Key != key || header.Software != AppServices.SoftwareFingerprint)
            { timing.Outcome = "identity-mismatch"; return null; }
            long start = file.Position;
            if (Convert.ToHexString(SHA256.HashData(file)) != header.PayloadSha256)
            { timing.Outcome = "corrupt"; return null; }
            file.Position = start;
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var value = typeof(T) == typeof(SolarTimeline) ? (T?)(object?)JsonSerializer.Deserialize<CompactSolar>(gzip)?.Expand() : JsonSerializer.Deserialize<T>(gzip);
            timing.Outcome = "hit"; return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        { timing.Outcome = "invalid"; return null; }
    }
    public void Write<T>(string stage, string key, T value)
    {
        using var timing = PerformanceTrace.Phase("cache-write", stage);
        string path = PathFor(stage), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
            {
                var header = new Header(key, AppServices.SoftwareFingerprint, new string('0', 64));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(header);
                if (bytes.Length > 4096) throw new ArgumentException("Cache identity is too long.");
                writer.Write(Magic); writer.Write(bytes.Length); writer.Write(bytes); writer.Flush();
                long start = file.Position;
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, true))
                {
                    if (value is SolarTimeline solar) JsonSerializer.Serialize(gzip, CompactSolar.From(solar));
                    else JsonSerializer.Serialize(gzip, value);
                }
                file.Position = start;
                var hash = Convert.ToHexString(SHA256.HashData(file));
                file.Position = sizeof(uint) + sizeof(int);
                writer.Write(JsonSerializer.SerializeToUtf8Bytes(header with { PayloadSha256 = hash }));
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
