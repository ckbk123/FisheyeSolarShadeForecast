using System.Globalization;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;

namespace SolarShade.Shading;

public sealed record SolarTimelineStage(SolarTimeline Timeline, IReadOnlyList<string> Artifacts);

public sealed partial class SolarPositionModule
{
    /// <summary>Consumes the source's explicit interval bounds. Quadrature does not create irradiance observations.</summary>
    public SolarTimeline PrepareIntervals(IrradianceDataset dataset, int samplesPerInterval = 60,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IrradianceDatasets.Validate(dataset);
        if (dataset.Latitude is { } latitude && Math.Abs(latitude - site.LatitudeDegrees) > 1e-8 ||
            dataset.Longitude is { } longitude && Math.Abs(longitude - site.LongitudeDegrees) > 1e-8)
            throw new ArgumentException("Solar site differs from the location recorded in the irradiance dataset.", nameof(dataset));
        if (samplesPerInterval is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(samplesPerInterval));
        var rows = new SolarIntervalGeometry[dataset.Intervals.Count];
        SolarGeometrySample Sample(DateTimeOffset timestamp, double weight)
        {
            var geometric = Calculate(timestamp);
            var apparent = SunPosition.FromGeometricNoaa(geometric.ZenithDegrees, geometric.AzimuthDegrees);
            return new(timestamp, geometric.ZenithDegrees, apparent.ZenithDegrees, geometric.AzimuthDegrees, weight);
        }
        IReadOnlyList<SolarGeometrySample> Midpoints(DateTimeOffset start, DateTimeOffset end)
        {
            var samples = new SolarGeometrySample[samplesPerInterval];
            for (int j = 0; j < samples.Length; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timestamp = start.AddTicks((long)((j + .5) * (end - start).Ticks / samples.Length));
                samples[j] = Sample(timestamp, 1.0 / samples.Length);
            }
            return Array.AsReadOnly(samples);
        }
        Parallel.For(0, rows.Length, new ParallelOptions { CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2)) }, i =>
        {
            var interval = dataset.Intervals[i];
            var full = Midpoints(interval.SourceStart, interval.SourceEnd);
            var selected = interval.Start == interval.SourceStart && interval.End == interval.SourceEnd
                ? full : Midpoints(interval.Start, interval.End);
            rows[i] = new(interval, Sample(interval.Timestamp, 1), full, selected);
        });
        return new(dataset, Array.AsReadOnly(rows),
            "Source interval labels and bounds preserved. Equal-weight midpoint quadrature; source interval and selected/clipped interval each have weights summing to one. " +
            "Geometric NOAA/Meeus topocentric angles; apparent zenith uses the explicit standard-atmosphere NOAA refraction approximation. " +
            "Azimuth clockwise from true north; zenith from vertical up. All rows, including diffuse-only and night, retain geometry.");
    }

    public SolarTimelineStage PrepareIntervalsToWorkbook(IrradianceDataset dataset, string debugDirectory,
        int samplesPerInterval = 60, CancellationToken cancellationToken = default)
    {
        var timeline = PrepareIntervals(dataset, samplesPerInterval, cancellationToken);
        return new(timeline, SolarIntervalWorkbook.Export(timeline, site, debugDirectory, cancellationToken));
    }
}

/// <summary>Exports existing geometry exactly; never evaluates another set of solar positions for diagnostics.</summary>
public static class SolarIntervalWorkbook
{
    public static IReadOnlyList<string> Export(SolarTimeline timeline, SolarSite site, string directory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        string metadata = $"{timeline.Convention} Site latitude {site.LatitudeDegrees.ToString("R", CultureInfo.InvariantCulture)}, longitude {site.LongitudeDegrees.ToString("R", CultureInfo.InvariantCulture)}, elevation {site.ElevationMetres.ToString("R", CultureInfo.InvariantCulture)} m. {timeline.Dataset.Source}; {timeline.Dataset.TimestampConvention}; zone {timeline.Dataset.TimeZoneId}.";
        string path = Path.Combine(directory, "solar-positions.xlsx");
        ScientificWorkbook.Write(path, "Solar positions",
            ["Timestamp (local, UTC offset)", "Solar zenith (degrees)", "Solar azimuth (degrees)", "Source interval ID", "Source start", "Source end", "Selected start", "Selected end", "Apparent zenith (degrees)", "Source quadrature samples", "Selected quadrature samples"],
            timeline.Intervals.Select(row => new object?[] { row.Interval.Timestamp, row.AtLabel.GeometricZenithDegrees,
                row.AtLabel.AzimuthDegrees, row.Interval.Id, row.Interval.SourceStart, row.Interval.SourceEnd,
                row.Interval.Start, row.Interval.End, row.AtLabel.ApparentZenithDegrees, row.SourceSamples.Count, row.Samples.Count }), metadata, cancellationToken);
        var artifacts = new List<string> { path };
        IEnumerable<object?[]> DetailRows()
        {
            foreach (var row in timeline.Intervals)
            {
                foreach (var set in new[] { (Name: "Source", Samples: row.SourceSamples), (Name: "Selected", Samples: row.Samples) })
                    foreach (var sample in set.Samples)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var direction = Direction.FromAngles(sample.AzimuthDegrees, sample.ApparentZenithDegrees);
                        yield return [row.Interval.Id, row.Interval.Timestamp, set.Name, sample.Timestamp,
                            sample.GeometricZenithDegrees, sample.ApparentZenithDegrees, sample.AzimuthDegrees,
                            direction.East, direction.North, direction.Up, sample.Weight,
                            row.Interval.SourceStart, row.Interval.SourceEnd, row.Interval.Start, row.Interval.End];
                    }
            }
        }
        int part = 0;
        foreach (var rows in DetailRows().Chunk(100_000))
        {
            string detail = Path.Combine(directory, part++ == 0 ? "solar-integration-samples.xlsx" : $"solar-integration-samples-{part:D3}.xlsx");
            ScientificWorkbook.Write(detail, "Integration samples",
                ["Source interval ID", "Source timestamp", "Quadrature domain", "Sample timestamp", "Geometric zenith (degrees)", "Apparent zenith (degrees)", "Azimuth (degrees)", "Apparent east unit vector", "Apparent north unit vector", "Apparent up unit vector", "Mean quadrature weight", "Source start", "Source end", "Selected start", "Selected end"],
                rows, metadata + " These are numerical integration samples, not additional weather observations.", cancellationToken);
            artifacts.Add(detail);
        }
        return artifacts.AsReadOnly();
    }
}
