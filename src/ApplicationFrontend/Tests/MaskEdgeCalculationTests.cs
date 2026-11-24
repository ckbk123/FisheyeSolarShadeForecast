using System.IO;
using OpenCvSharp;
using SolarShade.Camera;
using SolarShade.Desktop;
using Xunit;
using Xunit.Abstractions;

[Collection("Manual update UI")]
public sealed class MaskEdgeCalculationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PreparingAPreviewReadsOnlyThePhotoAndReusesItsMaskOnUpdate()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        using var service = new AppServices(fixture.Debug);
        var preview = await service.PrepareEdgeMask(fixture.Settings with { ImportPath = "missing-weather.xlsx", Zone = "invalid-zone" }, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(fixture.Debug, "run.json")));
        Assert.Equal(0, service.PreparationCount);
        Assert.Equal(0, service.CardinalGenerationCount);
        var result = await Evaluate(service, fixture.Settings);
        Assert.Same(preview, result.Mask);
        Assert.Equal(preview.SuggestEdge(CameraProfileFiles.Load(fixture.Settings.ProfilePath).Calibration, CancellationToken.None), result.MaskEdge);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareEdgeMask(fixture.Settings, new CancellationToken(true)));
    }

    [Fact]
    public async Task SelectedMaskControlsTheEffectiveFieldEnergyAndRestoredResult()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var settings = PrepareStudy(fixture);
        string openId = ImportMask(fixture, "Open annulus", 79);
        string blockedId = ImportMask(fixture, "All blocked", null);
        var extended = settings with { SelectedMaskId = openId };
        var baseline = extended with { ApparentFullFovDegrees = 120 };
        var blocked = extended with { SelectedMaskId = blockedId };
        using var service = new AppServices(fixture.Debug);

        var before = await Evaluate(service, baseline);
        Assert.Equal(120, before.MaskEdge!.ManualFullFovDegrees);
        Assert.Equal(0d, before.Run.AfterEnergy);
        Assert.True(before.Run.BeforeEnergy > 0);
        AssertProfile(fixture.Debug, 60, null);

        var open = await Evaluate(service, extended);
        var band = Assert.IsType<AppliedMaskEdge>(open.MaskEdge);
        Assert.Equal(95, band.BlackPercent);
        Assert.True(band.ObservedTerminalBand);
        Assert.Equal(120, band.MeasuredFullDegrees);
        Assert.InRange(band.EffectiveFullDegrees, 158, 162);
        Assert.Equal(0, band.ObservedSkyAtCap);
        Assert.Equal(0, band.UnknownAtCap);
        Assert.Equal(before.Run.BeforeEnergy, open.Run.BeforeEnergy);
        Assert.Equal(0.4, Assert.IsType<double>(open.Run.AfterEnergy), 7);
        Assert.True(open.Run.SkyCoverage > before.Run.SkyCoverage);
        AssertProfile(fixture.Debug, band.EffectiveFullDegrees / 2, 60);
        Assert.Equal(band, Manifest(fixture.Debug).MaskEdge);
        Assert.Equal(openId, Manifest(fixture.Debug).Settings.SelectedMaskId);
        output.WriteLine($"Measured {before.Run.AfterEnergy:F4} kWh/m², accepted {open.Run.AfterEnergy:F4} kWh/m² at {band.EffectiveFullDegrees:F2}° full. Coverage {before.Run.SkyCoverage:P2} → {open.Run.SkyCoverage:P2}.");

        service.ObserveInputs(blocked);
        Assert.False(service.IsCurrent(open));
        Assert.Null(service.TryRestoreAccepted(blocked));
        var dark = await Evaluate(service, blocked);
        Assert.True(dark.MaskEdge!.ObservedTerminalBand);
        Assert.Equal(120, dark.MaskEdge.EffectiveFullDegrees);
        Assert.Equal(0d, dark.Run.AfterEnergy);
        Assert.Equal(open.Run.BeforeEnergy, dark.Run.BeforeEnergy);
        Assert.Equal(blockedId, Manifest(fixture.Debug).Settings.SelectedMaskId);
        AssertProfile(fixture.Debug, 60, null);

        service.ObserveInputs(extended);
        var restored = Assert.IsType<Evaluation>(service.TryRestoreAccepted(extended));
        Assert.True(service.IsCurrent(restored));
        Assert.Equal(open.Run.AfterEnergy, restored.Run.AfterEnergy);
        Assert.Equal(open.Run.SkyCoverage, restored.Run.SkyCoverage);
        Assert.Equal(band, restored.MaskEdge);
        Assert.Equal(band, Manifest(fixture.Debug).MaskEdge);
        AssertProfile(fixture.Debug, band.EffectiveFullDegrees / 2, 60);

        var reset = await Evaluate(service, baseline);
        Assert.Equal(before.MaskEdge, reset.MaskEdge);
        Assert.Equal(before.MaskEdge, Manifest(fixture.Debug).MaskEdge);
        Assert.Equal(before.Run.Rows.ToArray(), reset.Run.Rows.ToArray());
        AssertProfile(fixture.Debug, 60, null);
    }

    [Fact]
    public async Task UneditedStudyUsesThe95PercentBandEvenWithAnOpenSliverAt180()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var settings = PrepareStudy(fixture);
        using var binary = new Mat(303, 201, MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(binary, new Point(100, 151), 70, Scalar.White, -1);
        Cv2.Line(binary, new Point(165, 151), new Point(195, 151), Scalar.White, 1);
        string png = Path.Combine(fixture.Root, "thin-horizon-opening.png");
        Cv2.ImWrite(png, binary);
        var variant = ManualMaskStore.Import(settings.SkyImage, png, "Small opening at cap", centeredDisk: true);
        using var service = new AppServices(fixture.Debug);
        var study = await Evaluate(service, settings with { SelectedMaskId = variant.Id });
        var proposal = Assert.IsType<AppliedMaskEdge>(study.MaskEdge);
        Assert.Equal(95, proposal.BlackPercent);
        Assert.True(proposal.ObservedTerminalBand);
        Assert.InRange(proposal.EffectiveFullDegrees, 139, 144);
        Assert.True(proposal.ObservedSkyAtCap > 0);
        Assert.Equal(0, proposal.UnknownAtCap);
        Assert.Equal(proposal.EffectiveFullDegrees, 2 * study.Cardinals!.Projection.MaximumIncidentAngleDegrees);
    }

    [Fact]
    public async Task AnExportedEffectiveProfileReopensWithTheProposalFromTheSelectedMask()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var settings = PrepareStudy(fixture) with { SelectedMaskId = ImportMask(fixture, "Open through 180", 96) };
        using var service = new AppServices(fixture.Debug);
        var extended = await Evaluate(service, settings);
        var cap = Assert.IsType<AppliedMaskEdge>(extended.MaskEdge);
        Assert.False(cap.ObservedTerminalBand);
        Assert.Equal(180, cap.EffectiveFullDegrees);
        Assert.Equal(1440, cap.ObservedSkyAtCap);
        Assert.Equal(0, cap.UnknownAtCap);
        Assert.True(extended.Run.AfterEnergy > 0);

        string exported = AppServices.Export(extended, Path.Combine(fixture.Root, "exports"));
        AssertProfile(exported, 90, 60);
        Assert.Equal(cap, Manifest(exported).MaskEdge);
        var reimported = settings with { ProfilePath = ProfilePath(exported) };
        using var reopened = new AppServices(Path.Combine(fixture.Root, "reopened study"));
        var proposed = await Evaluate(reopened, reimported);
        Assert.Equal(cap, proposed.MaskEdge);
        Assert.Equal(extended.Run.Rows.ToArray(), proposed.Run.Rows.ToArray());
        AssertProfile(proposed.DebugDirectory!, 90, 60);
        AssertProfile(exported, 90, 60);
        var newlyBlocked = await Evaluate(reopened, reimported with { SelectedMaskId = ImportMask(fixture, "Blocked after reimport", null) });
        Assert.Equal(120, newlyBlocked.MaskEdge!.EffectiveFullDegrees);
        Assert.Equal(0d, newlyBlocked.Run.AfterEnergy);
        AssertProfile(newlyBlocked.DebugDirectory!, 60, null);
    }

    private static UserSettings PrepareStudy(ManualUpdateTests.Fixture fixture)
    {
        var profile = CameraProfileFiles.Load(fixture.Settings.ProfilePath);
        CameraProfileFiles.Export(profile with { Calibration = profile.Calibration with
            { IncidentAngleToRadiusPolynomial = [0, 180 / Math.PI], MaximumIncidentAngleDegrees = 60 } }, fixture.Root);
        // At the equator this morning interval puts the Sun in the 60°–79° annulus.
        File.WriteAllLines(fixture.Settings.ImportPath, new[] { "Timestamp,BHI,DHI" }.Concat(
            Enumerable.Range(0, 24).Select(h => $"2025-05-15T{h:00}:00:00+00:00,{(h == 7 ? 400 : 0)},0")));
        return fixture.Settings with { Latitude = 0, Longitude = 0, CameraTilt = 0, PanelTilt = 0 };
    }

    private static string ImportMask(ManualUpdateTests.Fixture fixture, string name, int? outerRadius)
    {
        using var image = new Mat(303, 201, MatType.CV_8UC1, Scalar.Black);
        if (outerRadius is int r)
        {
            Cv2.Circle(image, new Point(100, 151), r, Scalar.White, -1);
            Cv2.Circle(image, new Point(100, 151), 60, Scalar.Black, -1);
        }
        string path = Path.Combine(fixture.Root, name + ".png");
        Cv2.ImWrite(path, image);
        return ManualMaskStore.Import(fixture.Settings.SkyImage, path, name, centeredDisk: true).Id;
    }

    private static Task<Evaluation> Evaluate(AppServices service, UserSettings settings) =>
        service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);

    private static string ProfilePath(string directory) => Path.Combine(directory, "01-calibration", "camera-profile.json");

    private static void AssertProfile(string directory, double maximumHalfAngle, double? measuredHalfAngle)
    {
        var calibration = CameraProfileFiles.Load(ProfilePath(directory)).Calibration;
        Assert.Equal(maximumHalfAngle, calibration.MaximumIncidentAngleDegrees, 7);
        Assert.Equal(measuredHalfAngle, calibration.LinearExtensionStartDegrees);
    }

    private static DebugDataRun.Manifest Manifest(string directory) =>
        Assert.IsType<DebugDataRun.Manifest>(AppData.Read<DebugDataRun.Manifest>(Path.Combine(directory, "run.json")));
}
