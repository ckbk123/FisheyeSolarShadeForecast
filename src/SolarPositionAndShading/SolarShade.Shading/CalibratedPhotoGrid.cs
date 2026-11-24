using System.Globalization;
using OpenCvSharp;

namespace SolarShade.Shading;

/// <summary>Native-pixel geometry for the lens rings and the two geographic vertical planes.</summary>
internal sealed class CalibratedPhotoGrid
{
    private readonly CalibratedSkyProjection projection;
    private readonly Point2d center;
    private readonly List<Point[]> arcs = [];
    private readonly List<Point2d> dots = [];
    private readonly List<(string Text, Point Origin, Point Degree)> captions = [];
    private readonly double scale, fontScale, dotRadius;
    private readonly int ringStroke;

    public IReadOnlyList<CardinalDirectionOverlayRing> Rings { get; }
    public bool LensCenterVisible { get; }
    public CardinalDirectionOverlayPoint? Zenith { get; }
    public int MeridianDotCount => dots.Count;

    public CalibratedPhotoGrid(CalibratedSkyProjection projection, List<Rect> occupied, CancellationToken ct)
    {
        this.projection = projection;
        center = new(projection.PrincipalPointX, projection.PrincipalPointY);
        LensCenterVisible = center.X >= 0 && center.X <= projection.Width - 1 && center.Y >= 0 && center.Y <= projection.Height - 1;
        if (projection.TryProject(new Direction(0, 0, 1), out double zx, out double zy)) Zenith = new(zx, zy);
        double fieldRadius = projection.RadiusAtIncidentAngle(projection.MaximumIncidentAngleRadians);
        scale = Math.Min(Math.Min(projection.Width, projection.Height), 4 * Math.Min(fieldRadius, projection.CoverageDisk.RadiusPixels));
        dotRadius = Math.Max(1.3, scale * .0036);
        ringStroke = Math.Max(1, (int)Math.Round(scale * .002));
        fontScale = Math.Max(.34, scale / 575);
        var rings = new List<CardinalDirectionOverlayRing>();
        // Exactly 180° maps to the undefined optical antipode, not to a circle of unique rays.
        for (int angle = 30; angle < 180 && angle * Math.PI / 180 <= projection.MaximumIncidentAngleRadians + 1e-14; angle += 30)
        {
            ct.ThrowIfCancellationRequested();
            double radius = projection.RadiusAtIncidentAngle(angle * Math.PI / 180);
            bool visible = AddRingArcs(radius);
            rings.Add(new(angle, radius, visible));
            if (visible) AddCaption(angle, radius, occupied);
        }
        Rings = rings.AsReadOnly();
        foreach (double azimuth in new[] { 0d, 90d, 180d, 270d }) AddMeridian(azimuth, ct);
        // Leave the actual lens-center pixel readable even when a compass plane crosses the lens.
        // The distinct zenith symbol is still drawn there when the optical axis points straight up.
        if (LensCenterVisible) dots.RemoveAll(p => double.Hypot(p.X - center.X, p.Y - center.Y) < 2 * dotRadius);
    }

    private Point2d OnRing(double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    private static Point Fixed(double x, double y) =>
        new(checked((int)Math.Round(x * 256)), checked((int)Math.Round(y * 256)));

    // Split the mathematical circle at every possible rectangle/disk crossing before sampling.
    // This preserves even small off-center arcs and avoids drawing chords across invisible gaps.
    private bool AddRingArcs(double radius)
    {
        var cuts = new List<double> { 0, Math.Tau };
        void AddIntersections(double angle, double cosine)
        {
            if (cosine < -1 || cosine > 1) return;
            double offset = Math.Acos(cosine);
            cuts.Add((angle + offset + Math.Tau) % Math.Tau);
            cuts.Add((angle - offset + Math.Tau) % Math.Tau);
        }
        AddIntersections(0, -center.X / radius);
        AddIntersections(0, (projection.Width - 1 - center.X) / radius);
        AddIntersections(Math.PI / 2, -center.Y / radius);
        AddIntersections(Math.PI / 2, (projection.Height - 1 - center.Y) / radius);
        var disk = projection.CoverageDisk;
        double dx = disk.CenterX - center.X, dy = disk.CenterY - center.Y, distance = double.Hypot(dx, dy);
        if (distance > 0)
            AddIntersections(Math.Atan2(dy, dx),
                (radius * radius + distance * distance - disk.RadiusPixels * disk.RadiusPixels) / (2 * radius * distance));
        cuts.Sort();
        bool visible = false;
        // At most 0.15 native pixel of sagitta, even on a high-resolution photograph.
        double maxStep = Math.Min(Math.PI / 12, 4 * Math.Asin(Math.Sqrt(Math.Min(1, .15 / (2 * radius)))));
        for (int i = 1; i < cuts.Count; i++)
        {
            double start = cuts[i - 1], end = cuts[i];
            if (end <= start) continue;
            var middle = OnRing(radius, (start + end) / 2);
            if (middle.X < 0 || middle.X > projection.Width - 1 || middle.Y < 0 || middle.Y > projection.Height - 1 ||
                double.Hypot(middle.X - disk.CenterX, middle.Y - disk.CenterY) > disk.RadiusPixels + 1e-8) continue;
            int count = Math.Max(1, (int)Math.Ceiling((end - start) / maxStep));
            var points = new Point[count + 1];
            for (int j = 0; j <= count; j++)
            {
                var p = OnRing(radius, start + (end - start) * j / count);
                points[j] = Fixed(p.X, p.Y);
            }
            arcs.Add(points);
            visible = true;
        }
        return visible;
    }

    private void AddCaption(int angle, double radius, List<Rect> occupied)
    {
        string text = angle.ToString(CultureInfo.InvariantCulture);
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, fontScale, ringStroke, out int baseline);
        int degreeRadius = Math.Max(1, (int)Math.Round(fontScale * 2.4));
        int width = size.Width + 3 * degreeRadius, height = size.Height + baseline;
        // Try diagonal sectors first. Text occupies the inside of its ring, away from the FOV outline.
        foreach (int bearing in new[] { -45, 45, -135, 135, -70, 70, -110, 110, 0, 180 })
        {
            double a = bearing * Math.PI / 180;
            double inset = Math.Abs(Math.Cos(a)) * width / 2 + Math.Abs(Math.Sin(a)) * height / 2 + 2 * ringStroke;
            if (radius <= inset) continue;
            var point = OnRing(radius - inset, a);
            var box = new Rect((int)Math.Round(point.X - width / 2d), (int)Math.Round(point.Y - height / 2d), width, height);
            if (!projection.ContainsCalibratedPixel(box.Left, box.Top) || !projection.ContainsCalibratedPixel(box.Right, box.Top) ||
                !projection.ContainsCalibratedPixel(box.Left, box.Bottom) || !projection.ContainsCalibratedPixel(box.Right, box.Bottom) ||
                occupied.Any(other => other.IntersectsWith(box))) continue;
            occupied.Add(box);
            captions.Add((text, new(box.Left, box.Top + size.Height), new(box.Left + size.Width + degreeRadius, box.Top + degreeRadius)));
            return;
        }
    }

    private void AddMeridian(double azimuth, CancellationToken ct)
    {
        const double step = .025;
        double spacing = dotRadius * 4.6, toNext = 0, lastX = 0, lastY = 0;
        Direction last = default;
        bool connected = false;
        for (int i = 0; i <= 7200; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            double zenith = i * step;
            var ray = Direction.FromAngles(azimuth, zenith);
            if (!projection.TryProject(ray, out double x, out double y)) { connected = false; continue; }
            if (!connected || !projection.CanConnectProjectedRays(last, ray))
            {
                dots.Add(new(x, y));
                toNext = spacing;
            }
            else
            {
                double length = double.Hypot(x - lastX, y - lastY);
                while (length > 0 && toNext <= length)
                {
                    // Project the interpolated direction itself. Connecting image points with a straight
                    // chord would cut across curved fisheye meridians, especially near the optical antipode.
                    var sample = Direction.FromAngles(azimuth, zenith - step + step * toNext / length);
                    if (projection.TryProject(sample, out double sx, out double sy)) dots.Add(new(sx, sy));
                    toNext += spacing;
                }
                toNext -= length;
            }
            connected = true; last = ray; lastX = x; lastY = y;
        }
    }

    public Mat Render(IReadOnlyList<CardinalDirectionOverlayMarker> markers, double opacity, double cardinalFont, int cardinalStroke, CancellationToken ct)
    {
        using var coverage = CoverageMask(ct);
        using var layer = new Mat(projection.Height, projection.Width, MatType.CV_8UC1, Scalar.All(0));
        var rgba = new Mat(layer.Size(), MatType.CV_8UC4, Scalar.All(0));
        try
        {
            var ink = Scalar.All(255);
            // A fine dark outline keeps the cyan scale readable over both blue sky and white cloud.
            for (int pass = 0; pass < 2; pass++)
            {
                ct.ThrowIfCancellationRequested();
                int extra = pass == 0 ? Math.Max(2, ringStroke) : 0;
                if (arcs.Count > 0) Cv2.Polylines(layer, arcs, false, ink, ringStroke + extra, LineTypes.AntiAlias, 8);
                foreach (var c in captions)
                {
                    Cv2.PutText(layer, c.Text, c.Origin, HersheyFonts.HersheySimplex, fontScale, ink, ringStroke + extra, LineTypes.AntiAlias);
                    Cv2.Circle(layer, c.Degree, Math.Max(1, (int)Math.Round(fontScale * 2.4)), ink, ringStroke + extra, LineTypes.AntiAlias);
                }
                Cv2.BitwiseAnd(layer, coverage, layer);
                if (LensCenterVisible)
                {
                    double arm = Math.Max(4, scale * .028);
                    int stroke = Math.Max(2, (int)Math.Round(scale * .0045)) + extra;
                    Cv2.Line(layer, Fixed(center.X - arm, center.Y), Fixed(center.X + arm, center.Y), ink, stroke, LineTypes.AntiAlias, 8);
                    Cv2.Line(layer, Fixed(center.X, center.Y - arm), Fixed(center.X, center.Y + arm), ink, stroke, LineTypes.AntiAlias, 8);
                }
                Composite(rgba, layer, pass == 0 ? new Vec4b(49, 35, 16, 255) : new Vec4b(245, 217, 0, 255));
                layer.SetTo(Scalar.All(0));
            }
            foreach (var p in dots) Cv2.Circle(layer, Fixed(p.X, p.Y), (int)Math.Round(dotRadius * 256), ink, -1, LineTypes.AntiAlias, 8);
            if (Zenith is { } z) Cv2.Circle(layer, Fixed(z.X, z.Y), (int)Math.Round(dotRadius * 2 * 256), ink, -1, LineTypes.AntiAlias, 8);
            Cv2.BitwiseAnd(layer, coverage, layer);
            foreach (var marker in markers)
                if (marker.LabelX is { } lx && marker.LabelY is { } ly)
                    Cv2.PutText(layer, marker.Name, new Point((int)lx, (int)ly), HersheyFonts.HersheySimplex, cardinalFont, ink, cardinalStroke, LineTypes.AntiAlias);
            Composite(rgba, layer, new Vec4b(180, 0, 255, 255));
            Cv2.ExtractChannel(rgba, layer, 3);
            layer.ConvertTo(layer, MatType.CV_8UC1, opacity);
            Cv2.InsertChannel(layer, rgba, 3);
            return rgba;
        }
        catch { rgba.Dispose(); throw; }
    }

    // Compose antialiased coverage before applying the user's overall opacity. A binary mask would
    // replace a dark outline with fully saturated cyan even where the top stroke only grazes a pixel.
    private static void Composite(Mat rgba, Mat layer, Vec4b color)
    {
        var layers = layer.AsRows<byte>();
        var pixels = rgba.AsRows<Vec4b>();
        int height = layer.Height;
        for (int y = 0; y < height; y++)
        {
            var coverage = layers[y];
            var row = pixels[y];
            int x = 0;
            while (x < coverage.Length)
            {
                int skip = coverage[x..].IndexOfAnyExcept((byte)0);
                if (skip < 0) break;
                x += skip;
                do
                {
                    byte top = coverage[x];
                    ref var pixel = ref row[x];
                    int back = pixel.Item3 * (255 - top);
                    if (back == 0) pixel = new(color.Item0, color.Item1, color.Item2, top);
                    else
                    {
                        int front = top * 255, alpha = front + back;
                        byte Mix(byte above, byte below) => (byte)((front * above + back * below + alpha / 2) / alpha);
                        pixel = new(Mix(color.Item0, pixel.Item0), Mix(color.Item1, pixel.Item1),
                            Mix(color.Item2, pixel.Item2), (byte)((alpha + 127) / 255));
                    }
                    x++;
                } while (x < coverage.Length && coverage[x] != 0);
            }
        }
    }

    private Mat CoverageMask(CancellationToken ct)
    {
        var mask = new Mat(projection.Height, projection.Width, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            var disk = projection.CoverageDisk;
            double maxRadius = projection.RadiusAtIncidentAngle(projection.MaximumIncidentAngleRadians);
            var rows = mask.AsRows<byte>();
            for (int y = 0; y < projection.Height; y++)
            {
                if ((y & 255) == 0) ct.ThrowIfCancellationRequested();
                double a = maxRadius * maxRadius - (y - center.Y) * (y - center.Y);
                double b = disk.RadiusPixels * disk.RadiusPixels - (y - disk.CenterY) * (y - disk.CenterY);
                if (a < 0 || b < 0) continue;
                double dx = Math.Sqrt(a), diskDx = Math.Sqrt(b);
                double left = Math.Ceiling(Math.Max(0, Math.Max(center.X - dx, disk.CenterX - diskDx)));
                double right = Math.Floor(Math.Min(projection.Width - 1, Math.Min(center.X + dx, disk.CenterX + diskDx)));
                if (right >= left)
                    rows[y].Slice((int)left, (int)(right - left + 1)).Fill(byte.MaxValue);
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }
}
