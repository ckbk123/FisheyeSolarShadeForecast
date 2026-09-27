using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolarShade.Core.Models;
using YamlDotNet.Serialization;

namespace SolarShade.Camera;

/// <summary>The effective profile used for projection. Coverage is explicit, never inferred from a fitted polynomial.</summary>
public sealed record CalibrationProfile(CalibrationResult Calibration, string Source, string CoverageNote)
{
    [JsonIgnore] public string? NativeYaml { get; init; }
}

public static class CameraProfileFiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static CalibrationProfile Load(string path, int width = 0, int height = 0, double? coverageOverride = null)
    {
        CalibrationProfile profile;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            profile = JsonSerializer.Deserialize<CalibrationProfile>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("The camera profile JSON is empty.");
            if (profile.Calibration is null) throw new InvalidDataException("Expected a camera profile containing Calibration.");
            profile = profile with { Source = path };
        }
        else
        {
            string yaml = File.ReadAllText(path);
            var map = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml)
                ?? throw new InvalidDataException("The calibration YAML is empty.");
            double[] Numbers(string name) => map.TryGetValue(name, out var value) && value is System.Collections.IEnumerable sequence && value is not string
                ? sequence.Cast<object>().Select(v => Convert.ToDouble(v, CultureInfo.InvariantCulture)).ToArray()
                : throw new InvalidDataException($"Missing calibration {name}.");
            if (map.ContainsKey("image_size"))
            {
                var size = Numbers("image_size");
                if (size.Length != 2 || size.Any(v => !double.IsFinite(v) || v < 2 || v > int.MaxValue || v != Math.Truncate(v)))
                    throw new InvalidDataException("Invalid calibration image_size.");
                if ((width > 0 && width != size[0]) || (height > 0 && height != size[1]))
                    throw new InvalidDataException("Calibration image_size does not match the EXIF-oriented mask. Recalibrate or transform the profile explicitly.");
                width = (int)size[0]; height = (int)size[1];
            }
            if (width < 2 || height < 2) throw new InvalidDataException("Legacy calibration YAML needs the oriented image dimensions.");
            if (coverageOverride is null or <= 0)
                throw new ArgumentException("Supply the explicitly validated maximum incident angle when loading YAML.", nameof(coverageOverride));
            var principal = Numbers("principal_point");
            var polynomial = Numbers("poly_incident_angle_to_radius");
            if (principal.Length != 2) throw new InvalidDataException("Principal point needs two values.");
            double radius = Math.Min(Math.Min(principal[0], width - 1 - principal[0]), Math.Min(principal[1], height - 1 - principal[1]));
            profile = new(new(2, CameraModelKind.OmniCalibIncidentAnglePolynomial, width, height, principal, polynomial,
                coverageOverride.Value, radius, null, 0, [], File.GetLastWriteTimeUtc(path),
                "Native OmniCalib YAML; caller-provided angular coverage"), path, "Caller-provided maximum incident angle; not inferred from lens advertising or polynomial extrapolation.") { NativeYaml = yaml };
        }
        if (coverageOverride is > 0)
            profile = profile with { Calibration = profile.Calibration with { MaximumIncidentAngleDegrees = coverageOverride.Value },
                CoverageNote = "Caller override of maximum incident angle. " + profile.CoverageNote };
        Validate(profile.Calibration, width, height);
        return profile;
    }

    public static void Validate(CalibrationResult calibration, int width = 0, int height = 0)
    {
        if (calibration.CameraModel != CameraModelKind.OmniCalibIncidentAnglePolynomial)
            throw new NotSupportedException("Unsupported camera model.");
        if (calibration.ImageWidth < 2 || calibration.ImageHeight < 2 ||
            (width > 0 && width != calibration.ImageWidth) || (height > 0 && height != calibration.ImageHeight))
            throw new InvalidDataException("Calibration dimensions do not match the oriented mask.");
        if (calibration.PrincipalPoint is not { Length: 2 } || calibration.PrincipalPoint.Any(v => !double.IsFinite(v)) ||
            calibration.IncidentAngleToRadiusPolynomial is not { Length: >= 2 } || calibration.IncidentAngleToRadiusPolynomial.Any(v => !double.IsFinite(v)) ||
            !double.IsFinite(calibration.MaximumIncidentAngleDegrees) || calibration.MaximumIncidentAngleDegrees is <= 0 or > 180 ||
            !double.IsFinite(calibration.ImageCircleRadiusPixels) || calibration.ImageCircleRadiusPixels <= 0)
            throw new InvalidDataException("Invalid effective camera calibration.");
    }

    /// <summary>Exports the same effective profile consumed by projection, retaining full native YAML when available.</summary>
    public static IReadOnlyList<string> Export(CalibrationProfile profile, string directory, CancellationToken cancellationToken = default)
    {
        Validate(profile.Calibration);
        cancellationToken.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string yamlPath = Path.Combine(directory, "calibration.yml"), jsonPath = Path.Combine(directory, "camera-profile.json");
        string Numbers(IEnumerable<double> values) => "[" + string.Join(", ", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
        var c = profile.Calibration;
        string yaml = profile.NativeYaml ?? "# Native OmniCalib projection parameters; ascending coefficients; radians; oriented zero-based xy\n" +
            "# Effective coverage and provenance are recorded in camera-profile.json.\n" +
            "principal_point: " + Numbers(c.PrincipalPoint) + "\n" +
            "poly_incident_angle_to_radius: " + Numbers(c.IncidentAngleToRadiusPolynomial) + "\n" +
            $"image_size: [{c.ImageWidth}, {c.ImageHeight}]\n";
        WriteAtomic(yamlPath, yaml, cancellationToken);
        WriteAtomic(jsonPath, JsonSerializer.Serialize(profile, Json), cancellationToken);
        return Array.AsReadOnly(new[] { yamlPath, jsonPath });
    }

    public static void WriteAtomic(string path, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
