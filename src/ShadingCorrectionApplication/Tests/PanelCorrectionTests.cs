using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using SolarShade.Core.Models;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;
using SolarShade.ShadingCorrection;
using Xunit;

public sealed class PanelCorrectionTests
{
    private static readonly SolarSite Site = new(10.8, 106.7, 0);
    private static readonly DateTimeOffset Noon = new(2025, 5, 15, 5, 0, 0, TimeSpan.Zero);
    private static PanelSkyScene Scene(bool white, bool half = false)
    {
        const int n = 501;
        var pixels = new byte[n * n];
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            pixels[y * n + x] = white && (!half || x >= n / 2) ? (byte)255 : (byte)0;
        var calibration = new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
            n, n, [250, 250], [0, 500 / Math.PI], 90, 250, null, 0, [], DateTimeOffset.UtcNow, "Synthetic equidistant");
        return new(new(n, n, pixels), calibration, new(), new(250, 250, 250));
    }

    private static SolarTimeline Timeline(double bhi = 500, double dhi = 140, int minutes = 60)
    {
        var data = IrradianceDatasets.FromSamples([new(Noon, bhi, dhi), new(Noon.AddMinutes(minutes), bhi, dhi)],
            TimeSpan.FromMinutes(minutes), TimestampLabel.Start, "Synthetic", "UTC", "Start-labeled means");
        return new SolarPositionModule(Site).PrepareIntervals(data, 60);
    }

    [Theory][InlineData(0)][InlineData(30)][InlineData(80)]
    public void OpenSkyMatchesExistingIndependentTransposition(double tilt)
    {
        var timeline = Timeline(); var panel = new PanelOrientation(tilt, 180);
        var prepared = TranspositionModule.ComputePrepared(timeline, panel);
        var actual = ShadingCorrectionModule.ApplyToPanel(prepared, Scene(true));
        var solar = new SolarPositionModule(Site);
        var reference = TranspositionModule.Compute(timeline.Dataset.Intervals.Select(r => new IrradianceSample(r.Timestamp, r.DirectHorizontal, r.DiffuseHorizontal)).ToArray(),
            panel, t => { var p = solar.Calculate(t); return SunPosition.FromGeometricNoaa(p.ZenithDegrees, p.AzimuthDegrees); }, SamplingWindow.FollowingHour(), DiffuseModel.HayDavies);
        Assert.True(reference.Succeeded, reference.Message);
        for (int i = 0; i < actual.Rows.Count; i++)
        {
            Assert.InRange(Math.Abs(actual.Rows[i].BeforeTotal - reference.Samples[i].Total), 0, 1e-8);
            Assert.InRange(Math.Abs(actual.Rows[i].AfterTotal!.Value - actual.Rows[i].BeforeTotal), 0, .03);
        }
    }

    [Fact] public void BlockedSkyIsZeroAndMissingSceneRemainsUnavailable()
    {
        var source = TranspositionModule.ComputePrepared(Timeline(), new(40, 0));
        var blocked = ShadingCorrectionModule.ApplyToPanel(source, Scene(false));
        Assert.All(blocked.Rows, r => { Assert.Equal(0, r.AfterTotal); Assert.True(r.BeforeTotal > 0); });
        var missing = ShadingCorrectionModule.ApplyToPanel(source, null);
        Assert.All(missing.Rows, r => Assert.Null(r.AfterTotal)); Assert.Null(missing.AfterEnergy); Assert.Null(missing.LossPercent);
    }

    [Fact] public void HalfSkyUsesReceiverOrientationAndReusesSolarMoments()
    {
        var scene = Scene(true, true);
        Assert.InRange(scene.Dome(new(0, 0, 1)).Visible, .49, .51);
        Assert.True(Math.Abs(scene.Dome(Direction.FromAngles(90, 60)).Visible - scene.Dome(Direction.FromAngles(270, 60)).Visible) > .3);
        var timeline = Timeline();
        _ = ShadingCorrectionModule.ApplyToPanel(TranspositionModule.ComputePrepared(timeline, new(30, 90)), scene);
        int firstCount = scene.CachedSolarDirections;
        _ = ShadingCorrectionModule.ApplyToPanel(TranspositionModule.ComputePrepared(timeline, new(60, 270)), scene);
        Assert.True(firstCount > 0); Assert.Equal(firstCount, scene.CachedSolarDirections);
    }

    [Fact] public void PanelVisibilityCannotBeReplacedByAveragedHorizontalFactor()
    {
        var timeline = Timeline(dhi: 0);
        var interval = timeline.Intervals[0].Interval;
        var samples = new[] { new SolarGeometrySample(Noon, 65, 65, 80, .5), new SolarGeometrySample(Noon.AddMinutes(30), 40, 40, 280, .5) };
        var synthetic = new SolarTimeline(timeline.Dataset with { Intervals = [interval] },
            [new(interval, samples[0], samples, samples)], "Synthetic changing solar direction");
        var scene = Scene(true, true);
        var horizontal = ShadingCorrectionModule.ApplyToPanel(TranspositionModule.ComputePrepared(synthetic, new(0, 0)), scene).Rows[0];
        var tilted = ShadingCorrectionModule.ApplyToPanel(TranspositionModule.ComputePrepared(synthetic, new(60, 270)), scene).Rows[0];
        Assert.True(Math.Abs(tilted.AfterDirect!.Value - tilted.BeforeDirect * horizontal.DirectTransmission!.Value) > 50);
    }

    [Theory][InlineData(15)][InlineData(60)]
    public void SourceCadenceAndActualDurationSurviveCorrectionAndNativeExports(int minutes)
    {
        var timeline = Timeline(0, 100, minutes);
        var source = TranspositionModule.ComputePrepared(timeline, new(0, 0));
        var run = ShadingCorrectionModule.ApplyToPanel(source, Scene(false));
        Assert.Equal(2, run.Rows.Count);
        Assert.Equal(2 * minutes / 60d * .1, run.BeforeEnergy, 10);
        Assert.Equal(100, run.LossPercent);
        for (int i = 0; i < 2; i++)
        { Assert.Equal(timeline.Dataset.Intervals[i].Id, run.Rows[i].IntervalId); Assert.Equal(TimeSpan.FromMinutes(minutes), run.Rows[i].End - run.Rows[i].Start); }
        var directory = Path.Combine(Path.GetTempPath(), "panel-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = ShadingCorrectionModule.ExportPanel(run, directory);
            Assert.Equal(4, paths.Count);
            Assert.Equal(new PanelOrientation(0, 0), run.Panel);
            Assert.Equal("Synthetic", run.Source);
            Assert.Equal(TimeSpan.FromMinutes(minutes), run.NativeCadence);
            Assert.Equal(IrradianceDatasets.Fingerprint(timeline.Dataset), run.DatasetFingerprint);
            using var zip = ZipFile.OpenRead(Path.Combine(directory, "panel-shaded.xlsx"));
            using (var meta = new StreamReader(zip.GetEntry("docProps/core.xml")!.Open()))
            { var text = meta.ReadToEnd(); Assert.Contains("TiltDegrees", text); Assert.Contains(run.DatasetFingerprint!, text); }
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cells = XDocument.Load(stream).Descendants(ns + "c").ToDictionary(c => (string)c.Attribute("r")!, c => c.Value);
            Assert.Equal(run.Rows[0].AfterTotal, double.Parse(cells["D2"], CultureInfo.InvariantCulture));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact] public void UndefinedFactorsAreBlankAndTransmissionRecoversShadedFlux()
    {
        Assert.Null(ShadingCorrectionModule.Transmission(0, 0)); Assert.Null(ShadingCorrectionModule.Transmission(50, null));
        Assert.Equal(25d, 100 * ShadingCorrectionModule.Transmission(100, 25));
    }

    [Fact] public void ClippedIntervalsKeepSourceBoundsAndIntegrateActualEnergy()
    {
        var original = Timeline(0, 100).Dataset;
        var selected = IrradianceDatasets.Select(original, Noon.AddMinutes(15), Noon.AddMinutes(45));
        var solar = new SolarPositionModule(Site).PrepareIntervals(selected, 15);
        var run = ShadingCorrectionModule.ApplyToPanel(TranspositionModule.ComputePrepared(solar, new(0, 0)), null);
        var row = Assert.Single(run.Rows);
        Assert.Equal(Noon, row.SourceStart); Assert.Equal(Noon.AddHours(1), row.SourceEnd);
        Assert.Equal(Noon.AddMinutes(15), row.Start); Assert.Equal(Noon.AddMinutes(45), row.End);
        Assert.Equal(.05, run.BeforeEnergy, 10);
    }

    [Fact] public void CancelledStageDoesNotClaimSavedArtifacts()
    {
        var source = TranspositionModule.ComputePrepared(Timeline(), new(30, 180));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var directory = Path.Combine(Path.GetTempPath(), "cancelled-panel-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<OperationCanceledException>(() => ShadingCorrectionModule.ApplyToPanel(source, null, directory, cancelled.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public void BottomBearingConversionIsLibraryOwned()
    { Assert.Equal(0, CameraPose.FromImageBottom(180).ImageTopAzimuthDegrees); Assert.Equal(270, CameraPose.FromImageBottom(90).ImageTopAzimuthDegrees); }

    [Fact] public void ResultRowsAreDefensiveReadOnlySnapshotsIncludingRecordCopies()
    {
        PanelRow[] input = [new(Noon, Noon, Noon.AddHours(1), 50, 50, 25, 25, 100)];
        var run = new PanelRun(input, 1, 0, "Isotropic");
        input[0] = input[0] with { BeforeDirect = 900 };
        Assert.Equal(100, run.Rows[0].BeforeTotal);
        Assert.Equal(.1, run.BeforeEnergy, 10);
        Assert.Throws<NotSupportedException>(() => ((IList<PanelRow>)run.Rows)[0] = input[0]);
        var copy = run with { Rows = input };
        input[0] = input[0] with { BeforeDirect = 0 };
        Assert.Equal(950, copy.Rows[0].BeforeTotal);
    }
}


