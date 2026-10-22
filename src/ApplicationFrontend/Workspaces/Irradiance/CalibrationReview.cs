using System.Text.Json;
using SolarShade.Calibration.Solver;
using SolarShade.Core.Models;
using SolarShade.Camera;

namespace SolarShade.Desktop;

public sealed record CalibrationReview(string Directory, CalibrationDetectionReport Report, string? Note = null, string? FitSummary = null)
{
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
        return new(directory, report, note, fit);
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
