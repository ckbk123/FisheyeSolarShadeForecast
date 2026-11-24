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
        Assert.Null(reopened.MaskEdge);
        Assert.Equal(baseline.Run.Rows.ToArray(), reopened.Run.Rows.ToArray());

        var extendedSettings = settings with { ApparentFullFovDegrees = 151.25, MaskEdgeThresholdPercent = 100 };
        var extended = await Calculate(extendedSettings);
        Assert.Equal(151.25, extended.MaskEdge!.ManualFullFovDegrees);
        Assert.Null(extended.MaskEdge.BlackPercent);
        Assert.True(extended.Run.AfterEnergy > baseline.Run.AfterEnergy);
        var profile = CameraProfileFiles.Load(Path.Combine(fixture.Debug, "01-calibration", "camera-profile.json")).Calibration;
        Assert.Equal(70, profile.LinearExtensionStartDegrees);
        Assert.Equal(75.625, profile.MaximumIncidentAngleDegrees);

        service.ObserveInputs(smaller);
        var restored = Assert.IsType<Evaluation>(service.TryRestoreAccepted(smaller));
        Assert.Equal(manual, restored.MaskEdge);
        Assert.Equal(trimmed.Run.Rows.ToArray(), restored.Run.Rows.ToArray());
        var reset = await Calculate(settings);
        Assert.Null(reset.MaskEdge);
        Assert.Equal(baseline.Run.Rows.ToArray(), reset.Run.Rows.ToArray());

        Task<Evaluation> Calculate(UserSettings study) => service.Evaluate(study, false, _ => { }, null, CancellationToken.None);
    }

    [Fact]
    public void BoundsRespectMeasuredWideLensesAndAnUnavailableTangent()
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var original = CameraProfileFiles.Load(fixture.Settings.ProfilePath).Calibration;
        var wide = original with { MaximumIncidentAngleDegrees = 117.5 };
        Assert.Equal(235, CalibrationRadialMap.MaximumApparentFullDegrees(wide));
        var apparent = CalibrationRadialMap.WithApparentFullFov(wide, 210);
        Assert.Equal(105, apparent.MaximumIncidentAngleDegrees);
        Assert.Null(apparent.LinearExtensionStartDegrees);
        Assert.Equal(wide, CalibrationRadialMap.MeasuredProfile(apparent));
        Assert.Throws<ArgumentOutOfRangeException>(() => CalibrationRadialMap.WithApparentFullFov(wide, 236));
        var noTangent = original with { IncidentAngleToRadiusPolynomial = [0, 100, -50] };
        Assert.Equal(140, CalibrationRadialMap.MaximumApparentFullDegrees(noTangent));
        foreach (double invalid in new[] { 0, double.NaN, double.PositiveInfinity, 141 })
            Assert.Throws<ArgumentOutOfRangeException>(() => CalibrationRadialMap.WithApparentFullFov(noTangent, invalid));
    }
}
