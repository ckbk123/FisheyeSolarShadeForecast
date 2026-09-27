using System.Diagnostics;
using OpenCvSharp;
using SolarShade.Irradiance;

namespace SolarShade.Shading;

public sealed record SunPathOverlayOptions
{
    /// <summary>Maximum projected point-to-polyline deviation before subpixel rasterization.</summary>
    public double SimplificationTolerancePixels { get; init; } = .35;
    public int StrokeWidthPixels { get; init; } = 3;
    public double Opacity { get; init; } = .5;
}

public sealed record SunPathOverlayVertex(int TrackId, DateOnly LocalDate, string IntervalId, int SampleIndex,
    DateTimeOffset Timestamp, double PixelX, double PixelY);
public sealed record SunPathOverlayTimings(double ProjectionMilliseconds, double SimplificationMilliseconds,
    double RenderMilliseconds, double EncodeMilliseconds, double TotalMilliseconds);
public sealed record SunPathOverlayResult(byte[] Png, int Width, int Height,
    IReadOnlyList<SunPathOverlayVertex> Vertices, SunPathOverlayTimings Timings,
    long SelectedSampleCount, long ProjectedSampleCount, int TrackCount, string DisplayTimeZoneId,
    string GeometryConvention, SunPathOverlayOptions Options)
{
    public bool HasPaths => Vertices.Count > 0;
    public string DatasetFingerprint { get; init; } = "";
    public long NightSampleCount { get; init; }
    public long UnprojectedSampleCount { get; init; }
}

/// <summary>Draws the prepared timeline's selected apparent solar samples. Does not evaluate solar positions,
/// infer weather samples, or render the physical solar disk. Projection is the same calibrated model as shading.</summary>
public static class SunPathOverlayGenerator
{
    private readonly record struct ProjectedPoint(double X, double Y, int IntervalIndex, int SampleIndex);

    public static IReadOnlyList<string> Export(SunPathOverlayResult result, string directory, CancellationToken ct = default)
        => SunPathOverlayExporter.Export(result, directory, ct);

    public static SunPathOverlayResult Generate(SolarTimeline timeline, CalibratedSkyProjection projection,
        TimeZoneInfo displayZone, SunPathOverlayOptions? options = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        ArgumentNullException.ThrowIfNull(timeline); ArgumentNullException.ThrowIfNull(projection); ArgumentNullException.ThrowIfNull(displayZone);
        options ??= new();
        if (!double.IsFinite(options.SimplificationTolerancePixels) || options.SimplificationTolerancePixels is < 0 or > 10 ||
            options.StrokeWidthPixels is < 1 or > 64 || !double.IsFinite(options.Opacity) || options.Opacity is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (timeline.Intervals.Count != timeline.Dataset.Intervals.Count) throw new ArgumentException("Timeline must retain all selected source intervals.");
        string fingerprint = IrradianceDatasets.Fingerprint(timeline.Dataset);
        var vertices = new List<SunPathOverlayVertex>();
        var paths = new List<Point[]>();
        var points = new List<ProjectedPoint>(2048);
        DateOnly? trackDate = null;
        DateOnly? cachedLocalDate = null;
        DateTimeOffset nextLocalDay = default;
        Direction previousRay = default;
        DateTimeOffset? previousTimestamp = null;
        DateTimeOffset? previousIntervalEnd = null;
        double previousSpacingTicks = 0;
        long selectedCount = 0, projectedCount = 0, nightCount = 0, unprojectedCount = 0;
        double simplifyMilliseconds = 0;
        var projectionTimer = Stopwatch.StartNew();

        void Flush()
        {
            if (points.Count == 0) return;
            var started = Stopwatch.GetTimestamp();
            var retained = Simplify(points, options.SimplificationTolerancePixels, ct);
            int track = paths.Count;
            var polyline = new Point[Math.Max(2, retained.Count)];
            for (int i = 0; i < retained.Count; i++)
            {
                var p = points[retained[i]]; var interval = timeline.Intervals[p.IntervalIndex]; var sample = interval.Samples[p.SampleIndex];
                // Eight fractional bits preserve subpixel placement; simplification is evaluated in native pixels.
                polyline[i] = new(checked((int)Math.Round(p.X * 256)), checked((int)Math.Round(p.Y * 256)));
                vertices.Add(new(track, trackDate!.Value, interval.Interval.Id, p.SampleIndex, sample.Timestamp, p.X, p.Y));
            }
            if (retained.Count == 1) polyline[1] = polyline[0];
            paths.Add(polyline); points.Clear();
            simplifyMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        for (int i = 0; i < timeline.Intervals.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var interval = timeline.Intervals[i];
            if (interval.Interval != timeline.Dataset.Intervals[i]) throw new ArgumentException("Timeline/source interval mismatch.");
            if (previousIntervalEnd is { } previousEnd && interval.Interval.Start > previousEnd) Flush();
            double spacingTicks = interval.Samples.Count == 0 ? 0 : (interval.Interval.End - interval.Interval.Start).Ticks / (double)interval.Samples.Count;
            if (interval.Samples.Count == 0) Flush();
            for (int j = 0; j < interval.Samples.Count; j++)
            {
                if ((j & 255) == 0) ct.ThrowIfCancellationRequested();
                selectedCount++; var sample = interval.Samples[j];
                if (sample.Timestamp < interval.Interval.Start || sample.Timestamp >= interval.Interval.End ||
                    previousTimestamp is { } previous && sample.Timestamp <= previous)
                    throw new ArgumentException("Selected solar samples must be ordered within their represented intervals.");
                if (previousTimestamp is { } prior && (sample.Timestamp - prior).Ticks > 1.5 * (spacingTicks + previousSpacingTicks) / 2) Flush();
                previousTimestamp = sample.Timestamp; previousSpacingTicks = spacingTicks;
                if (!double.IsFinite(sample.ApparentZenithDegrees) || sample.ApparentZenithDegrees < 0 ||
                    sample.ApparentZenithDegrees > 180 || !double.IsFinite(sample.AzimuthDegrees) || sample.AzimuthDegrees is < 0 or > 360)
                { unprojectedCount++; Flush(); continue; }
                if (sample.ApparentZenithDegrees >= 90) { nightCount++; Flush(); continue; }
                var ray = Direction.FromAngles(sample.AzimuthDegrees, sample.ApparentZenithDegrees);
                if (!projection.TryProject(ray, out var x, out var y)) { unprojectedCount++; Flush(); continue; }
                if (cachedLocalDate == null || sample.Timestamp >= nextLocalDay)
                {
                    cachedLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sample.Timestamp, displayZone).DateTime);
                    nextLocalDay = NextLocalDayBoundary(cachedLocalDate.Value, displayZone);
                }
                var date = cachedLocalDate.Value;
                if (trackDate is { } previousDate && date != previousDate) Flush();
                if (points.Count > 0 && !projection.CanConnectProjectedRays(previousRay, ray)) Flush();
                trackDate = date;
                previousRay = ray;
                points.Add(new(x, y, i, j)); projectedCount++;
            }
            previousIntervalEnd = interval.Interval.End;
        }
        Flush();
        double projectionMilliseconds = Math.Max(0, projectionTimer.Elapsed.TotalMilliseconds - simplifyMilliseconds);
        ct.ThrowIfCancellationRequested(); var renderTimer = Stopwatch.StartNew();
        // Rasterize only simplified paths, once in a batch. Alpha-only antialiasing keeps PNG RGB straight-alpha yellow.
        using var alpha = new Mat(projection.Height, projection.Width, MatType.CV_8UC1, Scalar.All(0));
        if (paths.Count > 0)
            Cv2.Polylines(alpha, paths, false, Scalar.All(Math.Round(options.Opacity * 255)), options.StrokeWidthPixels, LineTypes.AntiAlias, 8);
        using var rgba = new Mat(projection.Height, projection.Width, MatType.CV_8UC4, new Scalar(0, 210, 255, 0));
        Cv2.InsertChannel(alpha, rgba, 3);
        double renderMilliseconds = renderTimer.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested(); var encoding = Stopwatch.StartNew();
        Cv2.ImEncode(".png", rgba, out var png, [new ImageEncodingParam(ImwriteFlags.PngCompression, 1), new ImageEncodingParam(ImwriteFlags.PngStrategy, 3)]);
        double encodeMilliseconds = encoding.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();
        return new(png, projection.Width, projection.Height, vertices.AsReadOnly(),
            new(projectionMilliseconds, simplifyMilliseconds, renderMilliseconds, encodeMilliseconds, total.Elapsed.TotalMilliseconds),
            selectedCount, projectedCount, paths.Count, displayZone.Id, timeline.Convention, options)
        { DatasetFingerprint = fingerprint, NightSampleCount = nightCount, UnprojectedSampleCount = unprojectedCount };
    }

    private static DateTimeOffset NextLocalDayBoundary(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones jump at midnight or skip an entire local date. The next represented local day begins
        // at its earliest valid instant; ambiguous midnight uses the earlier (larger-offset) occurrence.
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(midnight) ? zone.GetAmbiguousTimeOffsets(midnight).Max() : zone.GetUtcOffset(midnight);
        return new(midnight, offset);
    }

    // Douglas–Peucker retains original points and guarantees every removed input point lies within tolerance
    // of its replacement segment. Endpoints of each date/coverage/interval fragment always survive.
    private static List<int> Simplify(List<ProjectedPoint> points, double tolerance, CancellationToken ct)
    {
        if (points.Count <= 2 || tolerance == 0) return Enumerable.Range(0, points.Count).ToList();
        var retained = new bool[points.Count]; retained[0] = retained[^1] = true;
        var stack = new Stack<(int Start, int End)>(); stack.Push((0, points.Count - 1));
        double limit = tolerance * tolerance;
        while (stack.TryPop(out var range))
        {
            ct.ThrowIfCancellationRequested();
            var a = points[range.Start]; var b = points[range.End];
            double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
            double maximum = limit; int farthest = -1;
            for (int i = range.Start + 1; i < range.End; i++)
            {
                var p = points[i];
                double t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
                double ex = p.X - a.X - t * dx, ey = p.Y - a.Y - t * dy;
                double distance = ex * ex + ey * ey;
                if (distance > maximum) { maximum = distance; farthest = i; }
            }
            if (farthest >= 0)
            { retained[farthest] = true; stack.Push((range.Start, farthest)); stack.Push((farthest, range.End)); }
        }
        var result = new List<int>();
        for (int i = 0; i < retained.Length; i++) if (retained[i]) result.Add(i);
        return result;
    }
}

