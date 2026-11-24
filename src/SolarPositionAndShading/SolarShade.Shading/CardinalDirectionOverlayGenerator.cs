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
        "N/E/S/W anchors mark the furthest resolved visible zenith on their upper-hemisphere constant-azimuth meridians. " +
        "Each straight tick is a compass glyph along the horizontal world bearing projected onto the camera's right/down axes, with its outer end at the anchor. " +
        "A dot marks an end-on bearing or an anchor with less than one pixel of room. " +
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
        var (right, down, _) = projection.Pose.Axes();
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
            // Require a resolved sky bearing even when a point-sized intersection touches the coverage boundary.
            double innerX = x, innerY = y;
            var previousRay = Direction.FromAngles(azimuth, zenith);
            double meridianSpan = 0;
            for (double z = zenith; z > 0 && meridianSpan < 1;)
            {
                z = Math.Max(0, z - SearchStepDegrees);
                var ray = Direction.FromAngles(azimuth, z);
                if (!projection.TryProject(ray, out innerX, out innerY) || !projection.CanConnectProjectedRays(previousRay, ray)) break;
                meridianSpan = double.Hypot(x - innerX, y - innerY);
                previousRay = ray;
            }
            if (meridianSpan < 1) { markers.Add(Missing("Visible meridian fragment is too short for an unambiguous tick.")); continue; }
            var horizonBearing = Direction.FromAngles(azimuth, 90);
            var points = CompassTick(projection, new(x, y), -horizonBearing.Dot(right), -horizonBearing.Dot(down), tickLength);
            // Place the label beyond the tick. At a dot the resolved meridian supplies a label direction.
            var inner = points.Length == 2 ? points[1] : new(innerX, innerY);
            double dx = x - inner.X, dy = y - inner.Y, magnitude = double.Hypot(dx, dy);
            dx /= magnitude;
            dy /= magnitude;
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
                status, Array.AsReadOnly(points)));
        }
        double geometryMs = timer.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested(); timer.Restart();
        using var alpha = new Mat(projection.Height, projection.Width, MatType.CV_8UC1, Scalar.All(0));
        var ink = Scalar.All(Math.Round(options.Opacity * 255));
        foreach (var marker in markers.Where(m => m.Visible))
        {
            ct.ThrowIfCancellationRequested();
            Point[] points = marker.TickPoints.Select(p => new Point(checked((int)Math.Round(p.X*256)), checked((int)Math.Round(p.Y*256)))).ToArray();
            if (points.Length == 1)
                Cv2.Circle(alpha, points[0], thickness * 128, ink, -1, LineTypes.AntiAlias, 8);
            else
                Cv2.Line(alpha, points[0], points[1], ink, thickness, LineTypes.AntiAlias, 8);
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

    private static CardinalDirectionOverlayPoint[] CompassTick(CalibratedSkyProjection projection,
        CardinalDirectionOverlayPoint anchor, double dx, double dy, double length)
    {
        // All anchors depend on lens coverage. The glyph direction depends only on the bearing and camera frame,
        // so the two ends of a compass axis remain parallel even when their visible sky rays are above the horizon.
        double magnitude = double.Hypot(dx, dy);
        if (magnitude < 1e-12) return [anchor];
        dx /= magnitude;
        dy /= magnitude;
        double radius = double.Hypot(anchor.X - projection.PrincipalPointX, anchor.Y - projection.PrincipalPointY);
        length = Math.Min(length, radius);
        bool InCoverage(double distance) => projection.ContainsCalibratedPixel(anchor.X + dx * distance, anchor.Y + dy * distance);
        if (!InCoverage(length))
        {
            // The calibrated circle, detected disk and image rectangle have a convex intersection.
            double low = 0, high = length;
            for (int i = 0; i < 32; i++)
            {
                double mid = (low + high) / 2;
                if (InCoverage(mid)) low = mid;
                else high = mid;
            }
            length = low;
        }
        return length < 1 ? [anchor] : [anchor, new(anchor.X + dx * length, anchor.Y + dy * length)];
    }
}
