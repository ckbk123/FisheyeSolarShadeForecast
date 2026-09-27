
using System.IO;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SolarShade.SkyPhotoMasking;

namespace SolarShade.Desktop;

public sealed record UserSettings
{
    public bool FirstRun { get; set; }
    public string CalibrationFolder { get; set; } = "";
    public string SkyFolder { get; set; } = "";
    public string SkyImage { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public string ImportPath { get; set; } = "";
    public int Columns { get; set; } = 6;
    public int Rows { get; set; } = 9;
    public double SquareMm { get; set; } = 22;
    public double BottomAzimuth { get; set; } = 180;
    public double CameraTilt { get; set; }
    public double CameraRoll { get; set; }
    public double PanelTilt { get; set; } = 30;
    public double PanelAzimuth { get; set; } = 180;
    public double Latitude { get; set; } = 10.8;
    public double Longitude { get; set; } = 106.7;
    public double Elevation { get; set; }
    public DateTime Start { get; set; } = new(2025, 5, 1);
    public DateTime End { get; set; } = new(2025, 5, 31);
    public string Zone { get; set; } = TimeZoneInfo.Local.Id;
    // UI preference; Zone in an evaluation snapshot is the resolved, stable time-zone ID.
    // Older settings have no mode flag and adopt the automatic default on upgrade.
    public bool UseSystemTimeZone { get; set; } = true;
    public int Provider { get; set; }
    public int ImportWindow { get; set; } // 0 preceding, 1 following, 2 centered
    public double ImportIntervalMinutes { get; set; } = 60;
    public SkyModel Model { get; set; } = SkyModel.EfficientNetB5;
    public int Resolution { get; set; } = 1024;
    public double CoverageAngle { get; set; } // zero: use saved profile value
    public bool CenteredDisk { get; set; }
    public int Substeps { get; set; } = 60;
    public bool Isotropic { get; set; }
    public bool ShowSunPath { get; set; } = true;
    public bool ShowCardinalDirections { get; set; } = true;
}

public static class AppData
{
    public static string Root { get; set; } = Environment.GetEnvironmentVariable("SOLARSHADE_DATA_DIR") ??
        Path.Combine(AppContext.BaseDirectory, "Data");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string PathFor(string name) { Directory.CreateDirectory(Root); return Path.Combine(Root, name); }
    public static UserSettings? ReadSettings() => Read<UserSettings>(PathFor("settings.json"));
    public static void SaveSettings(UserSettings settings) => Write(PathFor("settings.json"), PortablePaths.Map(settings, path => PortablePaths.Store(path)));
    public static T? Read<T>(string path)
    { try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; } catch (JsonException) { return default; } catch (IOException) { return default; } }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); break; }
                // Sync clients and readers can briefly hold a file without delete sharing.
                // Keep the previous complete document intact while retrying replacement.
                catch (Exception ex) when (attempt < 7 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(Math.Min(200, 20 << attempt)); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Key(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static string FileKey(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static string LibraryVersion(Type type) => type.Assembly.ManifestModule.ModuleVersionId.ToString();
    public static string EnsureModel(SkyModel model)
    {
        string name = SkyPhotoMasker.ModelFilename(model);
        string directory = PathFor("models-v1"); Directory.CreateDirectory(directory);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Models." + name);
        if (stream == null)
        {
            string development = Path.Combine(AppContext.BaseDirectory, "SkyPhotoModels", name);
            if (File.Exists(development)) return Path.GetDirectoryName(development)!;
            throw new FileNotFoundException("This build does not contain the selected model. Use the packaged APPLICATION.exe.");
        }
        string path = Path.Combine(directory, name);
        if (File.Exists(path) && new FileInfo(path).Length == stream.Length)
        {
            using var existing = File.OpenRead(path);
            bool matches = SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(stream));
            if (matches) return directory;
            stream.Position = 0;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary)) stream.CopyTo(file);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return directory;
    }
}

