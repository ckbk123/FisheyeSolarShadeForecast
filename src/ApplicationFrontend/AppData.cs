
using System.IO;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SolarShade.SkyPhotoMasking;

namespace SolarShade.Desktop;

public sealed record RefinedEdgeSources(string PhotoSha256, string ProfileSha256)
{
    internal static RefinedEdgeSources? From(InputDependencies? inputs) =>
        inputs is { Image.HasDigest: true, Profile.HasDigest: true }
            ? new(inputs.Image.Content, inputs.Profile.Content) : null;

    internal bool HasReplacement(InputDependencies current) =>
        current.Image.HasDigest && current.Image.Content != PhotoSha256 ||
        current.Profile.HasDigest && current.Profile.Content != ProfileSha256;
}

public sealed partial record UserSettings
{
    public bool FirstRun { get; set; }
    public string CalibrationFolder { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FailedCalibrationAttempt? LastCalibrationFailure { get; set; }
    public string SkyFolder { get; set; } = "";
    public string SkyImage { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SourceIdentity? ActiveSkyPhoto { get; set; }
    public string ProfilePath { get; set; } = "";
    public string ImportPath { get; set; } = "";
    // Null keeps legacy settings: an import path selects imports; otherwise use services.
    public bool? UseDataServices { get; set; }
    public string RememberedImportPath { get; set; } = "";
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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? ApparentFullFovDegrees { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RefinedEdgeSources? ApparentFovSources { get; set; }

    internal void SetRefinedEdge(double? fullDegrees, InputDependencies? sources = null)
    {
        ApparentFullFovDegrees = fullDegrees;
        ApparentFovSources = fullDegrees.HasValue ? RefinedEdgeSources.From(sources) : null;
    }

    internal bool VerifyRefinedEdge(InputDependencies sources)
    {
        if (ApparentFovSources != null)
        {
            if (ApparentFullFovDegrees.HasValue && !ApparentFovSources.HasReplacement(sources)) return false;
            SetRefinedEdge(null);
            return true;
        }
        // Keep fits saved by older versions and bind them once both inputs are available.
        if (!ApparentFullFovDegrees.HasValue || RefinedEdgeSources.From(sources) is not { } identity) return false;
        ApparentFovSources = identity;
        return true;
    }

    public bool CenteredDisk { get; set; }
    public int Substeps { get; set; } = 60;
    public bool Isotropic { get; set; }
    public bool ShowSunPath { get; set; } = true;
    public bool ShowCardinalDirections { get; set; } = true;
    public bool NoMaskSelected { get; set; }
    public string? SelectedMaskId { get; set; }
    public Dictionary<string, string> LastMaskByPhoto { get; set; } = new();
}

public static class AppData
{
    public static string Root { get; set; } = Environment.GetEnvironmentVariable("SOLARSHADE_DATA_DIR") ??
        Path.Combine(AppContext.BaseDirectory, "Data");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string PathFor(string name) { Directory.CreateDirectory(Root); return Path.Combine(Root, name); }
    public static UserSettings? ReadSettings()
    {
        var settings = Read<UserSettings>(PathFor("settings.json"));
        // Older releases allowed hand fits through the native field on cameras wider than 270°.
        if (settings?.ApparentFullFovDegrees is > SolarShade.Core.Models.CalibrationRadialMap.MaximumRefinedFullDegrees and <= 360)
            settings.ApparentFullFovDegrees = SolarShade.Core.Models.CalibrationRadialMap.MaximumRefinedFullDegrees;
        return settings;
    }
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

