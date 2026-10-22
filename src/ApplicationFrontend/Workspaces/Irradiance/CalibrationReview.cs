using System.Text.Json;
using SolarShade.Calibration.Solver;
using SolarShade.Core.Models;
using SolarShade.Camera;
using SolarShade.Calibration.Validation;

namespace SolarShade.Desktop;

public sealed record CalibrationReview(string Directory, CalibrationDetectionReport Report, string? Note = null, string? FitSummary = null)
{
    public string EvidenceFingerprint { get; init; } = "";
    public string Summary => $"{Report.Images.Count(i => i.Detected)} detected · " +
        (Note == null ? $"{Report.Images.Count(i => !i.Detected)} failed" : "failed-photo count unavailable") +
        $" · {Report.InnerColumns} × {Report.InnerRows} inner corners";

    public static CalibrationReview? TryLoad(string directory)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            string report = Path.Combine(directory, "checkerboard-detections.json");
            if (File.Exists(report))
            {
                var data = JsonSerializer.Deserialize<CalibrationDetectionReport>(File.ReadAllText(report));
                return data is { SchemaVersion: 1, Images: not null } ? WithFit(directory, data) : null;
            }
            // Existing saved calibrations already contain observed/fitted corner overlays.
            string observations = Path.Combine(directory, "observations.json");
            if (!File.Exists(observations)) return null;
            var old = JsonSerializer.Deserialize<CalibrationObservationDocument>(File.ReadAllText(observations));
            if (old is not { InnerCornersColumnsRows.Length: >= 2, Images: not null }) return null;
            var images = old.Images.Select((image, index) => new CalibrationImageDetection(image.Name, image.Path,
                true, image.PointsXy.Length, $"Detected {image.PointsXy.Length} inner corners. Green circles: observed; red crosses: fitted.",
                Path.Combine("debug", $"{index:D3}-{Path.GetFileNameWithoutExtension(image.Name)}.jpg"))).ToArray();
            return WithFit(directory, new(1, old.InnerCornersColumnsRows[0], old.InnerCornersColumnsRows[1], images),
                "This saved calibration predates per-photo failure feedback; only detected boards were retained.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return null; }
    }

    public static CalibrationReview? ForProfile(string profilePath)
    {
        var review = TryLoad(Path.GetDirectoryName(PortablePaths.Resolve(profilePath))!);
        if (review?.MatchesProfile(profilePath) != true) return null;
        return TryLoad(review.Directory)?.EvidenceFingerprint == review.EvidenceFingerprint ? review : null;
    }
    public bool MatchesProfile(string profilePath)
    {
        try
        {
            profilePath = PortablePaths.Resolve(profilePath);
            if (Report.Failure != null) return false;
            if (File.Exists(Path.Combine(Directory, CalibrationEvidenceFiles.FileName)))
                return CalibrationEvidenceFiles.MatchesProfile(Directory, profilePath);
            // Older saved runs have no binding file. Check the saved fitted projection rather than
            // assuming that any profile next to an observations file belongs to that fit.
            string referencePath = Path.Combine(Directory, "result.json");
            string canonicalPath = Path.Combine(Directory, "camera-profile.json");
            if (!File.Exists(referencePath) || !File.Exists(canonicalPath)) return false;
            var reference = AppData.Read<CalibrationReferenceSolution>(referencePath);
            var canonical = CameraProfileFiles.Load(canonicalPath).Calibration;
            var selected = CameraProfileFiles.Load(profilePath, canonical.ImageWidth, canonical.ImageHeight,
                Path.GetExtension(profilePath).Equals(".json", StringComparison.OrdinalIgnoreCase) ? null : canonical.MaximumIncidentAngleDegrees).Calibration;
            return reference?.ImageSizeWidthHeight is { Length: 2 } size && size[0] == selected.ImageWidth && size[1] == selected.ImageHeight &&
                reference.PrincipalPointXy.SequenceEqual(selected.PrincipalPoint) &&
                reference.IncidentAngleToRadiusPolynomial.SequenceEqual(selected.IncidentAngleToRadiusPolynomial) &&
                selected.PrincipalPoint.SequenceEqual(canonical.PrincipalPoint) &&
                selected.IncidentAngleToRadiusPolynomial.SequenceEqual(canonical.IncidentAngleToRadiusPolynomial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or FormatException or OverflowException or YamlDotNet.Core.YamlException)
        { return false; }
    }

    private static CalibrationReview WithFit(string directory, CalibrationDetectionReport report, string? note = null)
    {
        string? fit = null;
        string profile = Path.Combine(directory, "camera-profile.json");
        if (File.Exists(profile))
        {
            try
            {
                var saved = CameraProfileFiles.Load(profile);
                fit = $"Fit RMSE {saved.Calibration.RmsError:F2} px · maximum incident angle {saved.Calibration.MaximumIncidentAngleDegrees:F2}°. {saved.CoverageNote}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        }
        var evidence = new[] { "checkerboard-detections.json", "observations.json", "result.json", "camera-profile.json", "calibration.yml", CalibrationEvidenceFiles.FileName }
            .Concat(report.Images.Select(i => i.PreviewFile).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(relative =>
            {
                string path = Path.GetFullPath(Path.Combine(directory, relative));
                string within = Path.GetRelativePath(directory, path);
                if (Path.IsPathRooted(within) || within == ".." || within.StartsWith(".." + Path.DirectorySeparatorChar))
                    throw new InvalidDataException("Calibration review evidence must stay inside its saved directory.");
                return new { File = relative, Hash = File.Exists(path) ? AppData.FileKey(path) : "missing" };
            }).ToArray();
        return new(directory, report, note, fit) { EvidenceFingerprint = AppData.Key(new { Report = report, Fit = fit, Files = evidence }) };
    }

    public byte[]? Preview(CalibrationImageDetection image)
    {
        if (string.IsNullOrWhiteSpace(image.PreviewFile)) return null;
        string path = Path.GetFullPath(Path.Combine(Directory, image.PreviewFile));
        string relative = Path.GetRelativePath(Directory, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return null;
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
