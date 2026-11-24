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
            $"projection image-top reference {result.Pose.ImageTopAzimuthDegrees} degrees clockwise from true north before roll, signed tilt {result.Pose.TiltDegrees} degrees from up toward that reference, image roll {result.Pose.RollDegrees} degrees around the lens. " +
            "Cyan lens grid and magenta compass meridians in a straight-alpha PNG.";
        string[] gridHeaders = ["Feature", "Incident angle (degrees)", "Radius (native pixels)", "X (native pixels)", "Y (native pixels)", "Visible"];
        var gridRows = new List<object?[]>
        {
            new object?[] { "Lens center", 0, 0, result.Projection.PrincipalPointX, result.Projection.PrincipalPointY, result.LensCenterVisible },
            new object?[] { "True zenith", null, null, result.Zenith?.X, result.Zenith?.Y, result.Zenith is not null }
        };
        gridRows.AddRange(result.Rings.Select(r => new object?[] { "Lens ring", r.IncidentAngleDegrees, r.RadiusPixels,
            result.Projection.PrincipalPointX, result.Projection.PrincipalPointY, r.Visible }));
        ScientificWorkbook.Write(workbook, [new("Cardinal directions", headers, rows), new("Lens grid", gridHeaders, gridRows)], description, ct);
        AtomicWrite(details, JsonSerializer.SerializeToUtf8Bytes(new
        {
            result.Width, result.Height, result.Pose, result.Projection, result.Markers, result.LensCenterVisible, result.Zenith,
            result.Rings, result.MeridianDotCount, result.Options, result.Timings, result.GeometryConvention,
            PoseConvention = "Projection inputs: ImageTopAzimuthDegrees is the pre-roll image-top bearing clockwise from true north, TiltDegrees is the signed optical-axis rotation from up toward that bearing, and RollDegrees rotates the image around the lens.",
            Color = "Cyan (#00D9F5) lens center and rings; magenta (#FF00B4) compass meridians, labels and zenith; straight-alpha PNG", AnnotationOnly = true,
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
