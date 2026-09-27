using System.Text.Json;
using SolarShade.Irradiance;

namespace SolarShade.Shading;

public static class CardinalDirectionOverlayExporter
{
    public static IReadOnlyList<string> Export(CardinalDirectionOverlayResult result, string directory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result); ct.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string image = Path.Combine(directory, "cardinal-directions-overlay.png");
        string workbook = Path.Combine(directory, "cardinal-directions.xlsx");
        string details = Path.Combine(directory, "cardinal-directions-details.json");
        AtomicWrite(image, result.Png, ct);
        string[] headers = ["Direction", "True azimuth (degrees)", "Visible tick", "Endpoint zenith (degrees)",
            "Endpoint x (native pixels)", "Endpoint y (native pixels)", "Label baseline x", "Label baseline y", "Status"];
        var rows = result.Markers.Select(m => new object?[] {m.Name, m.AzimuthDegrees, m.Visible, m.BoundaryZenithDegrees,
            m.BoundaryX, m.BoundaryY, m.LabelX, m.LabelY, m.Status});
        string description = $"{result.GeometryConvention} Native image {result.Width}x{result.Height}; " +
            $"image-top heading {result.Pose.ImageTopAzimuthDegrees} degrees, tilt {result.Pose.TiltDegrees} degrees, roll {result.Pose.RollDegrees} degrees. " +
            "Transparent magenta display annotations; no solar or irradiance calculation.";
        ScientificWorkbook.Write(workbook, [new("Cardinal directions", headers, rows)], description, ct);
        AtomicWrite(details, JsonSerializer.SerializeToUtf8Bytes(new
        {
            result.Width, result.Height, result.Pose, result.Projection, result.Markers, result.Options, result.Timings, result.GeometryConvention,
            Color = "Magenta (#FF00B4), straight-alpha PNG", AnnotationOnly = true,
            Artifacts = new[] {Path.GetFileName(image), Path.GetFileName(workbook)}
        }, new JsonSerializerOptions {WriteIndented = true}), ct);
        return new[] {image, workbook, details};
    }

    private static void AtomicWrite(string path, byte[] bytes, CancellationToken ct)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { ct.ThrowIfCancellationRequested(); File.WriteAllBytes(temporary, bytes); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
