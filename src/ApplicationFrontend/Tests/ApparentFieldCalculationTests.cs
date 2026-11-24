using System.IO;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Desktop;
using Xunit;

[Collection("Manual update UI")]
public sealed class ApparentFieldCalculationTests
{
    [Fact]
    public async Task ManualFieldChangesCoverageAndEnergyAndRestoresTheAcceptedStudy()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        using var service = new AppServices(fixture.Debug);
        var settings = fixture.Settings;
        var baseline = await Calculate(settings);
        var smaller = settings with { ApparentFullFovDegrees = 92.5 };
        var trimmed = await Calculate(smaller);
        var manual = Assert.IsType<AppliedMaskEdge>(trimmed.MaskEdge);
        Assert.Equal(140, manual.MeasuredFullDegrees);
        Assert.Equal(92.5, manual.ManualFullFovDegrees);
        Assert.Equal(92.5, manual.EffectiveFullDegrees);
        Assert.Null(manual.BlackPercent);
        Assert.Null(manual.UnknownAtCap);
        Assert.Null(manual.ObservedSkyAtCap);
        Assert.False(manual.ObservedTerminalBand);
        Assert.Equal(baseline.Run.BeforeEnergy, trimmed.Run.BeforeEnergy);
        Assert.True(trimmed.Run.AfterEnergy < baseline.Run.AfterEnergy);
        Assert.True(trimmed.Run.SkyCoverage < baseline.Run.SkyCoverage);
        Assert.Equal(46.25, trimmed.Cardinals!.Projection.MaximumIncidentAngleDegrees, 7);
        var manifest = AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))!;
        Assert.Equal(manual, manifest.MaskEdge);

        // Read both standalone effective artifacts, then select the exported JSON in a new study.
        string exported = AppServices.Export(trimmed, Path.Combine(fixture.Root, "export"));
        string folder = Path.Combine(exported, "01-calibration");
        foreach (string name in new[] { "camera-profile.json", "calibration.yml" })
        {
            var loaded = CameraProfileFiles.Load(Path.Combine(folder, name)).Calibration;
            Assert.Equal(46.25, loaded.MaximumIncidentAngleDegrees);
            Assert.Equal(70, loaded.OriginalMaximumIncidentAngleDegrees);
            Assert.Null(loaded.LinearExtensionStartDegrees);
            Assert.Equal(70, CalibrationRadialMap.MeasuredProfile(loaded).MaximumIncidentAngleDegrees);
        }
        var reopened = await Calculate(settings with { ProfilePath = Path.Combine(folder, "camera-profile.json") });
        Assert.Equal(baseline.MaskEdge, reopened.MaskEdge);
        Assert.Equal(baseline.Run.Rows.ToArray(), reopened.Run.Rows.ToArray());

        var extendedSettings = settings with { ApparentFullFovDegrees = 151.25 };
        var extended = await Calculate(extendedSettings);
        Assert.Equal(151.25, extended.MaskEdge!.ManualFullFovDegrees);
        Assert.Null(extended.MaskEdge.BlackPercent);
        Assert.True(extended.Run.AfterEnergy > trimmed.Run.AfterEnergy);
        var profile = CameraProfileFiles.Load(Path.Combine(fixture.Debug, "01-calibration", "camera-profile.json")).Calibration;
        Assert.Equal(70, profile.LinearExtensionStartDegrees);
        Assert.Equal(75.625, profile.MaximumIncidentAngleDegrees);

        service.ObserveInputs(smaller);
        var restored = Assert.IsType<Evaluation>(service.TryRestoreAccepted(smaller));
        Assert.Equal(manual, restored.MaskEdge);
        Assert.Equal(trimmed.Run.Rows.ToArray(), restored.Run.Rows.ToArray());
        var reset = await Calculate(settings);
        Assert.Equal(95, reset.MaskEdge!.BlackPercent);
        Assert.Equal(baseline.MaskEdge, reset.MaskEdge);
        Assert.Equal(baseline.Run.Rows.ToArray(), reset.Run.Rows.ToArray());

        Task<Evaluation> Calculate(UserSettings study) => service.Evaluate(study, false, _ => { }, null, CancellationToken.None);
    }

    [Fact]
    public async Task NativeFieldWiderThan270KeepsItsMeasuredProfileAndOffersTheSameEdgeAsTheCalculation()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var profile = CameraProfileFiles.Load(fixture.Settings.ProfilePath);
        CameraProfileFiles.Export(profile with { Calibration = profile.Calibration with { MaximumIncidentAngleDegrees = 150 } }, fixture.Root);
        using var service = new AppServices(fixture.Debug);
        var run = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        var proposal = run.Mask!.SuggestEdge(CameraProfileFiles.Load(fixture.Settings.ProfilePath).Calibration, CancellationToken.None);
        Assert.Equal(300, proposal.MeasuredFullDegrees);
        Assert.Equal(270, proposal.EffectiveFullDegrees);
        Assert.Null(proposal.BlackPercent);
        Assert.Null(proposal.ManualFullFovDegrees);
        Assert.Equal(proposal, run.MaskEdge);
        Assert.Equal(135, run.Cardinals!.Projection.MaximumIncidentAngleDegrees);
        var effective = CameraProfileFiles.Load(Path.Combine(fixture.Debug, "01-calibration", "camera-profile.json")).Calibration;
        Assert.Equal(150, CalibrationRadialMap.MeasuredProfile(effective).MaximumIncidentAngleDegrees);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(152.25, 152.25)]
    [InlineData(285d, 270d)]
    public void LegacyThresholdIsIgnoredAndSupportedSavedRefinementsArePreserved(double? saved, double? expected)
    {
        using var fixture = new ManualUpdateTests.Fixture();
        AppData.Write(AppData.PathFor("settings.json"), new { MaskEdgeThresholdPercent = 100, ApparentFullFovDegrees = saved });
        var study = AppData.ReadSettings()!;
        Assert.Equal(expected, study.ApparentFullFovDegrees);
        AppData.SaveSettings(study);
        Assert.DoesNotContain("MaskEdgeThresholdPercent", File.ReadAllText(AppData.PathFor("settings.json")));
    }

    [Fact]
    public void BoundsRespectMeasuredWideLensesAndAnUnavailableTangent()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var original = CameraProfileFiles.Load(fixture.Settings.ProfilePath).Calibration;
        var wide = original with { MaximumIncidentAngleDegrees = 117.5 };
        Assert.Equal(270, CalibrationRadialMap.MaximumApparentFullDegrees(wide));
        var apparent = CalibrationRadialMap.WithApparentFullFov(wide, 210);
        Assert.Equal(105, apparent.MaximumIncidentAngleDegrees);
        Assert.Null(apparent.LinearExtensionStartDegrees);
        Assert.Equal(wide, CalibrationRadialMap.MeasuredProfile(apparent));
        var rear = CalibrationRadialMap.WithApparentFullFov(wide, 270);
        Assert.Equal(135, rear.MaximumIncidentAngleDegrees);
        Assert.Equal(117.5, rear.LinearExtensionStartDegrees);
        Assert.Throws<ArgumentOutOfRangeException>(() => CalibrationRadialMap.WithApparentFullFov(wide, 271));
        var noTangent = original with { IncidentAngleToRadiusPolynomial = [0, 100, -50] };
        Assert.Equal(140, CalibrationRadialMap.MaximumApparentFullDegrees(noTangent));
        foreach (double invalid in new[] { 0, double.NaN, double.PositiveInfinity, 141 })
            Assert.Throws<ArgumentOutOfRangeException>(() => CalibrationRadialMap.WithApparentFullFov(noTangent, invalid));
    }
}
