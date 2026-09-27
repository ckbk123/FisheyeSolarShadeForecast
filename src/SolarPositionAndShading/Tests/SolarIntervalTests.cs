using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using SolarShade.Irradiance;
using SolarShade.Shading;
using Xunit;

public sealed class SolarIntervalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "solar-interval-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly SolarSite Site = new(10.8, 106.7, 20);
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-05-31T00:00:00+07:00");

    [Theory] [InlineData(15)] [InlineData(60)] [InlineData(37)]
    public void NativeCadenceLabelsAndNightGeometryArePreserved(int minutes)
    {
        var data = IrradianceDatasets.FromSamples([new(Start, 0, 20), new(Start.AddMinutes(minutes), 0, 0)],
            TimeSpan.FromMinutes(minutes), TimestampLabel.Start, "fixture", "Asia/Ho_Chi_Minh", "Interval start");
        var timeline = new SolarPositionModule(Site).PrepareIntervals(data, 5);
        Assert.Equal(data.Intervals.Count, timeline.Intervals.Count);
        for (int i = 0; i < timeline.Intervals.Count; i++)
        {
            var row = timeline.Intervals[i];
            Assert.Same(data.Intervals[i], row.Interval);
            Assert.Equal(5, row.Samples.Count);
            Assert.Equal(row.Interval.Start.AddMinutes(minutes / 10.0), row.Samples[0].Timestamp);
            Assert.Equal(1, row.Samples.Sum(s => s.Weight), 12);
            Assert.Equal(row.Interval.Timestamp, row.AtLabel.Timestamp);
            Assert.Same(row.SourceSamples, row.Samples);
            Assert.All(row.Samples, s => Assert.True(double.IsFinite(s.GeometricZenithDegrees)));
        }
    }

    [Fact]
    public void ClippedIntegrationRetainsWholeSourceGeometryForDniInference()
    {
        var data = IrradianceDatasets.FromSamples([new(Start, 0, 0)], TimeSpan.FromHours(1), TimestampLabel.Start,
            "fixture", "UTC", "Start");
        var selected = IrradianceDatasets.Select(data, Start.AddMinutes(10), Start.AddMinutes(25));
        var row = Assert.Single(new SolarPositionModule(Site).PrepareIntervals(selected, 2).Intervals);
        Assert.Equal(new[] { Start.AddMinutes(15), Start.AddMinutes(45) }, row.SourceSamples.Select(s => s.Timestamp));
        Assert.Equal(new[] { Start.AddMinutes(13.75), Start.AddMinutes(21.25) }, row.Samples.Select(s => s.Timestamp));
        Assert.Equal(data.Intervals[0].Id, row.Interval.Id);
        Assert.Equal(TimeSpan.FromHours(1), row.Interval.SourceEnd - row.Interval.SourceStart);
    }

    [Fact]
    public void VariableDurationsAndGapsAreNotStretchedOrFilled()
    {
        var a = new IrradianceInterval("first", Start, Start, Start.AddMinutes(15), Start, Start.AddMinutes(15), 0, 1);
        var b = new IrradianceInterval("after gap", Start.AddMinutes(45), Start.AddMinutes(45), Start.AddMinutes(70), Start.AddMinutes(45), Start.AddMinutes(70), 0, 1);
        var data = new IrradianceDataset([a, b], "explicit", "UTC", "Explicit", TimestampLabel.Explicit, null);
        var timeline = new SolarPositionModule(Site).PrepareIntervals(data, 1);
        Assert.Equal(2, timeline.Intervals.Count);
        Assert.Equal(Start.AddMinutes(7.5), timeline.Intervals[0].Samples[0].Timestamp);
        Assert.Equal(Start.AddMinutes(57.5), timeline.Intervals[1].Samples[0].Timestamp);
    }

    [Fact]
    public void ExportContainsTheReturnedLabelAndIntegrationValuesWithoutRecalculation()
    {
        var data = IrradianceDatasets.FromSamples([new(Start, 0, 20), new(Start.AddMinutes(15), 0, 20)],
            TimeSpan.FromMinutes(15), TimestampLabel.Start, "fixture", "UTC", "Start");
        var timeline = new SolarPositionModule(Site).PrepareIntervals(data, 2);
        var artifacts = SolarIntervalWorkbook.Export(timeline, Site, directory);
        Assert.Equal(2, artifacts.Count);
        var rows = ReadRows(artifacts[0]);
        Assert.Equal(3, rows.Length);
        for (int i = 0; i < timeline.Intervals.Count; i++)
        {
            var source = timeline.Intervals[i]; var cells = rows[i + 1];
            Assert.Equal(source.Interval.Timestamp, DateTimeOffset.Parse(cells[0], CultureInfo.InvariantCulture));
            Assert.Equal(source.AtLabel.GeometricZenithDegrees, double.Parse(cells[1], CultureInfo.InvariantCulture));
            Assert.Equal(source.Interval.Id, cells[3]);
        }
        var detail = ReadRows(artifacts[1]);
        Assert.Equal(9, detail.Length); // two rows x (two full + two selected samples), plus header
        Assert.Equal(timeline.Intervals[0].SourceSamples[0].Timestamp, DateTimeOffset.Parse(detail[1][3], CultureInfo.InvariantCulture));
        Assert.Equal(timeline.Intervals[0].SourceSamples[0].ApparentZenithDegrees, double.Parse(detail[1][5], CultureInfo.InvariantCulture));
        Assert.Equal(.5, double.Parse(detail[1][10], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CancellationDoesNotPublishAStageWorkbook()
    {
        var data = IrradianceDatasets.FromSamples([new(Start, 0, 0)], TimeSpan.FromMinutes(15), TimestampLabel.Start, "fixture", "UTC", "Start");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SolarPositionModule(Site).PrepareIntervalsToWorkbook(data, directory, 3, cancelled.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void RecordedSourceLocationCannotBeSilentlyReassigned()
    {
        var data = IrradianceDatasets.FromSamples([new(Start, 0, 20)], TimeSpan.FromMinutes(15), TimestampLabel.Start,
            "fixture", "UTC", "Start") with { Latitude = Site.LatitudeDegrees, Longitude = Site.LongitudeDegrees };
        Assert.Single(new SolarPositionModule(Site).PrepareIntervals(data, 1).Intervals);
        Assert.Throws<ArgumentException>(() => new SolarPositionModule(Site with { LongitudeDegrees = Site.LongitudeDegrees + 1 }).PrepareIntervals(data, 1));
        Assert.Throws<ArgumentException>(() => new SolarPositionModule(Site with { LatitudeDegrees = Site.LatitudeDegrees + 1 }).PrepareIntervals(data, 1));
    }

    [Theory] [InlineData(15)] [InlineData(60)]
    public void LegacyFileAdapterReadsNewDebugWorkbookWithoutLosingSourceCadence(int minutes)
    {
        var data = IrradianceDatasets.FromSamples([new(Start, 0, 20), new(Start.AddMinutes(minutes), 0, 25)],
            TimeSpan.FromMinutes(minutes), TimestampLabel.End, "fixture", "UTC", "Interval end")
            with { Latitude = Site.LatitudeDegrees, Longitude = Site.LongitudeDegrees };
        string path = Path.Combine(directory, "horizontal-irradiance.xlsx");
        IrradianceDatasetFiles.Export(data, path);
        var legacy = ShadingWorkbook.ReadIrradiance(path);
        Assert.Equal(minutes, legacy.SuggestedSampling!.DurationMinutes);
        Assert.Equal(-minutes, legacy.SuggestedSampling.StartOffsetMinutes);
        Assert.Equal(data.Intervals.Select(r => r.Timestamp), legacy.Samples.Select(s => s.TimestampUtc));
        Assert.Equal(data.Latitude, legacy.Latitude);
        Assert.Equal(data.Longitude, legacy.Longitude);
        Assert.Throws<ArgumentException>(() => ShadingFilePipeline.Compute(path, "unused-mask", "unused-profile", 65, Site,
            Path.Combine(directory, "unused.xlsx"), new TimeSampling(0, minutes, 2)));
    }

    [Fact]
    public void LegacyUniformWindowAdapterRejectsUnrepresentableClipping()
    {
        var data = IrradianceDatasets.FromSamples([new(Start, 0, 20)], TimeSpan.FromHours(1), TimestampLabel.Start,
            "fixture", "UTC", "Start");
        var selected = IrradianceDatasets.Select(data, Start.AddMinutes(15), Start.AddMinutes(30));
        string path = Path.Combine(directory, "clipped.xlsx");
        IrradianceDatasetFiles.Export(selected, path);
        var error = Assert.Throws<InvalidDataException>(() => ShadingWorkbook.ReadIrradiance(path));
        Assert.Contains("PrepareIntervals", error.Message);
    }

    private static string[][] ReadRows(string path)
    {
        using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return XDocument.Load(stream).Descendants(n + "row").Select(r => r.Elements(n + "c")
            .Select(c => c.Element(n + "v")?.Value ?? string.Concat(c.Descendants(n + "t").Select(t => t.Value))).ToArray()).ToArray();
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
