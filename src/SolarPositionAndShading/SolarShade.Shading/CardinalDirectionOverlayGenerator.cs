using System.Diagnostics;
using OpenCvSharp;

namespace SolarShade.Shading;

public sealed record CardinalDirectionOverlayOptions
{
    public double Opacity { get; init; } = .85;
}

public sealed record CardinalDirectionOverlayPoint(double X, double Y);
public sealed record CardinalDirectionOverlayMarker(string Name, double AzimuthDegrees, bool Visible,
    double? BoundaryZenithDegrees, double? BoundaryX, double? BoundaryY,
    double? LabelX, double? LabelY, string Status, IReadOnlyList<CardinalDirectionOverlayPoint> TickPoints);
public sealed record CardinalDirectionOverlayTimings(double GeometryMilliseconds, double RenderMilliseconds,
    double EncodeMilliseconds, double TotalMilliseconds);
public sealed record CardinalDirectionProjectionMetadata(double PrincipalPointX, double PrincipalPointY,
    IReadOnlyList<double> IncidentAngleToRadiusPolynomial, double MaximumIncidentAngleDegrees, ImageDisk ImageDisk);
public sealed record CardinalDirectionOverlayResult(byte[] Png, int Width, int Height,
    IReadOnlyList<CardinalDirectionOverlayMarker> Markers, CameraPose Pose,
    CardinalDirectionOverlayTimings Timings, CardinalDirectionOverlayOptions Options, string GeometryConvention,
    CardinalDirectionProjectionMetadata Projection)
{
    public bool HasMarkers => Markers.Any(m => m.Visible);
}

/// <summary>Orientation annotations from constant-world-azimuth meridians in the upper hemisphere.
/// Uses the shading projection unchanged; does not calculate sun positions or assume the horizon is visible.</summary>
public static class CardinalDirectionOverlayGenerator
{
    // Four bounded searches, independent of weather/solar sampling. A boundary bracket is subsequently bisected.
    private const double SearchStepDegrees = .025;
    public const string Convention = "EXIF-oriented native pixels; x right, y down; true azimuth clockwise from North. " +
        "N/E/S/W ticks follow upper-hemisphere constant-azimuth meridians toward their furthest resolved visible zenith. " +
        "Endpoints obey calibrated incident angle, actual image disk, and image rectangle. " +
        "An endpoint at zenith 90 degrees is the horizon; other endpoints are coverage limits, not horizon locations. " +
        "Default upward camera: N top, E left, S bottom, W right. Labels remain upright. " +
        "Meridian search spacing 0.025 degrees; boundary refinement 40 bisections. Subpixel raster precision 1/256 pixel.";

    public static IReadOnlyList<string> Export(CardinalDirectionOverlayResult result, string directory, CancellationToken ct = default)
        => CardinalDirectionOverlayExporter.Export(result, directory, ct);

    public static CardinalDirectionOverlayResult Generate(CalibratedSkyProjection projection,
        CardinalDirectionOverlayOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        options ??= new();
        if (!double.IsFinite(options.Opacity) || options.Opacity is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        ct.ThrowIfCancellationRequested();
        var total = Stopwatch.StartNew();
        var timer = Stopwatch.StartNew();
        double scale = Math.Min(projection.Width, projection.Height);
        // Portrait photo previews can be only ~170 display pixels wide: keep glyph height near 8%
        // of the image's short side so native-resolution annotations remain readable after downsampling.
        double tickLength = Math.Max(4, scale * .04);
        double fontScale = Math.Max(.4, scale / 260);
        int thickness = Math.Max(1, (int)Math.Round(scale / 200));
        var markers = new List<CardinalDirectionOverlayMarker>(4);
        var labels = new List<Rect>();
        foreach (var (name, azimuth) in new[] { ("N", 0d), ("E", 90d), ("S", 180d), ("W", 270d) })
        {
            ct.ThrowIfCancellationRequested();
            double zenith = -1, x = 0, y = 0;
            // Search from the horizon inward. This also handles the zenith being outside the camera view.
            for (int i = 0; i <= 3600; i++)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                double candidate = 90 - i * SearchStepDegrees;
                if (!projection.TryProject(Direction.FromAngles(azimuth, candidate), out x, out y)) continue;
                zenith = candidate;
                if (i > 0)
                {
                    double high = candidate + SearchStepDegrees;
                    for (int j = 0; j < 40; j++)
                    {
                        double mid = (zenith + high) / 2;
                        if (projection.TryProject(Direction.FromAngles(azimuth, mid), out _, out _)) zenith = mid;
                        else high = mid;
                    }
                    projection.TryProject(Direction.FromAngles(azimuth, zenith), out x, out y);
                }
                break;
            }
            CardinalDirectionOverlayMarker Missing(string status) => new(name, azimuth, false, null, null, null, null, null, status, []);
            if (zenith <= 0) { markers.Add(Missing("No resolved visible meridian segment.")); continue; }
            var points = new List<CardinalDirectionOverlayPoint> { new(x, y) };
            double length = 0;
            var previousRay = Direction.FromAngles(azimuth, zenith);
            // Keep ticks on their actual (possibly curved) meridian and within one visible fragment.
            for (double z = Math.Max(0, zenith - SearchStepDegrees); ; z = Math.Max(0, z - SearchStepDegrees))
            {
                var ray = Direction.FromAngles(azimuth, z);
                if (!projection.TryProject(ray, out double px, out double py) || !projection.CanConnectProjectedRays(previousRay, ray)) break;
                var last = points[^1];
                double distance = Math.Sqrt((px-last.X)*(px-last.X)+(py-last.Y)*(py-last.Y));
                points.Add(new(px, py)); length += distance; previousRay = ray;
                if (length >= tickLength || z == 0) break;
            }
            if (length < 1) { markers.Add(Missing("Visible meridian fragment is too short for an unambiguous tick.")); continue; }
            var inner = points[Math.Min(points.Count - 1, 4)];
            double dx = x - inner.X, dy = y - inner.Y, magnitude = Math.Sqrt(dx*dx+dy*dy);
            dx /= magnitude; dy /= magnitude;
            var size = Cv2.GetTextSize(name, HersheyFonts.HersheySimplex, fontScale, thickness, out int baseline);
            double clearance = Math.Abs(dx) * size.Width/2 + Math.Abs(dy) * size.Height/2 + Math.Max(3, scale * .008);
            int left = (int)Math.Round(x + dx*clearance - size.Width/2d);
            int top = (int)Math.Round(y + dy*clearance - size.Height/2d);
            // If a marker ends at the rectangular image edge, put its label inside without shifting its tick.
            left = Math.Clamp(left, thickness, Math.Max(thickness, projection.Width-size.Width-thickness-1));
            top = Math.Clamp(top, thickness, Math.Max(thickness, projection.Height-size.Height-baseline-thickness-1));
            var label = new Rect(left, top, size.Width + thickness, size.Height + baseline + thickness);
            bool fits = label.Right < projection.Width && label.Bottom < projection.Height &&
                !labels.Any(r => r.Left < label.Right && r.Right > label.Left && r.Top < label.Bottom && r.Bottom > label.Top);
            if (fits) labels.Add(label);
            string status = zenith >= 90 ? "Horizon endpoint." : "Calibrated coverage boundary.";
            if (!fits) status += " Label omitted: insufficient nonoverlapping image space.";
            markers.Add(new(name, azimuth, true, zenith, x, y, fits ? left : null, fits ? top+size.Height : null,
                status, points.AsReadOnly()));
        }
        double geometryMs = timer.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested(); timer.Restart();
        using var alpha = new Mat(projection.Height, projection.Width, MatType.CV_8UC1, Scalar.All(0));
        var ink = Scalar.All(Math.Round(options.Opacity * 255));
        foreach (var marker in markers.Where(m => m.Visible))
        {
            ct.ThrowIfCancellationRequested();
            Point[] points = marker.TickPoints.Select(p => new Point(checked((int)Math.Round(p.X*256)), checked((int)Math.Round(p.Y*256)))).ToArray();
            Cv2.Polylines(alpha, new[] { points }, false, ink, thickness, LineTypes.AntiAlias, 8);
            if (marker.LabelX is { } lx && marker.LabelY is { } ly)
                Cv2.PutText(alpha, marker.Name, new Point((int)lx, (int)ly), HersheyFonts.HersheySimplex, fontScale, ink, thickness, LineTypes.AntiAlias);
        }
        using var rgba = new Mat(projection.Height, projection.Width, MatType.CV_8UC4, new Scalar(180, 0, 255, 0));
        Cv2.InsertChannel(alpha, rgba, 3);
        double renderMs = timer.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested(); timer.Restart();
        Cv2.ImEncode(".png", rgba, out var png, [new ImageEncodingParam(ImwriteFlags.PngCompression, 1), new ImageEncodingParam(ImwriteFlags.PngStrategy, 3)]);
        double encodeMs = timer.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();
        return new(png, projection.Width, projection.Height, markers.AsReadOnly(), projection.Pose,
            new(geometryMs, renderMs, encodeMs, total.Elapsed.TotalMilliseconds), options, Convention,
            new(projection.PrincipalPointX, projection.PrincipalPointY, projection.IncidentAnglePolynomial,
                projection.MaximumIncidentAngleRadians * 180 / Math.PI, projection.CoverageDisk));
    }
}
