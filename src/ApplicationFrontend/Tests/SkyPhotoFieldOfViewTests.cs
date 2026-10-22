using System.IO;
using OpenCvSharp;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Desktop;
using SolarShade.Shading;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class SkyPhotoFieldOfViewTests
{
    private static CalibrationProfile Profile(double half = 90, double[]? principal = null, double[]? polynomial = null) =>
        new(new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial, 201, 303,
            principal ?? [100, 151], polynomial ?? [0, 70], half, 95, null, 0, [], DateTimeOffset.UnixEpoch, "Test lens"),
            "Synthetic", "Explicit test coverage.");

    [Fact]
    public void FullFovIsTwiceTheValidatedHalfAngleAndUnmaskedCoverageIsUnconfirmed()
    {
        var view = SkyPhotoFieldOfView.Describe(Profile(70), 201, 303, null);
        Assert.Equal(140, view.CalibratedFullDegrees); Assert.Null(view.CircularFullDegrees);
        Assert.Contains("140° full (70° half-angle)", view.Text);
        Assert.Contains("coverage unconfirmed", view.Text);
    }

    [Theory]
    [InlineData(100, 151, 95, 95)] // centered lens disk limits coverage
    [InlineData(110, 151, 95, 85)] // disk offset limits the complete cone
    [InlineData(100, 151, 120, 100)] // image edges limit coverage
    [InlineData(210, 151, 95, 0)] // optical axis outside the disk
    public void UsableCircularFovHonorsTheActualDiskAndImageEdges(double x, double y, double radius, double expectedRadius)
    {
        var view = SkyPhotoFieldOfView.Describe(Profile(), 201, 303, new(x, y, radius));
        Assert.Equal(180, view.CalibratedFullDegrees);
        Assert.Equal(2 * expectedRadius / 70 * 180 / Math.PI, view.CircularFullDegrees!.Value, 8);
    }

    [Fact]
    public void UsableFovCannotExceedTheCalibratedAngleAndInvalidProjectionIsRejected()
    {
        Assert.Equal(120, SkyPhotoFieldOfView.Describe(Profile(60), 201, 303, new(100, 151, 120)).CircularFullDegrees!.Value, 8);
        Assert.Throws<InvalidDataException>(() => SkyPhotoFieldOfView.Describe(Profile(), 200, 303, null));
        Assert.Throws<ArgumentException>(() => SkyPhotoFieldOfView.Describe(Profile(polynomial: [1, 70]), 201, 303, null));
    }

    [Fact]
    public void ReadoutTracksProfileOverrideReplacementAndInvalidationWithoutCalculating() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            Assert.Contains("140° full", window.Irradiance.FieldOfViewTextForTest);
            Assert.Contains("usable circular FOV", window.Irradiance.FieldOfViewTextForTest);
            window.IrradianceController.Settings.CoverageAngle = 55;
            window.Irradiance.SetCameraPoseForTest(180, 0, 0); await window.InputsSettled;
            Assert.Contains("110° full (55° half-angle)", window.Irradiance.FieldOfViewTextForTest);
            window.IrradianceController.Settings.CoverageAngle = 0;
            CameraProfileFiles.Export(Profile(60), fixture.Root);
            await window.RecheckSourcesAsync();
            Assert.Contains("120° full", window.Irradiance.FieldOfViewTextForTest);
            File.WriteAllText(fixture.Settings.ProfilePath, "invalid profile");
            await window.RecheckSourcesAsync();
            Assert.Equal("FOV unavailable", window.Irradiance.FieldOfViewTextForTest);
            Assert.Null(window.Completed); Assert.Equal(0, service.CardinalGenerationCount);
            Assert.False(Directory.Exists(Path.Combine(AppData.Root, "models-v1")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PhotoSwitchDoesNotReusePreviousDimensionsOrClaimUnmeasuredCoverage() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        string sameSize = Path.Combine(fixture.Root, "unmasked.png"), wrongSize = Path.Combine(fixture.Root, "wrong-size.png");
        using (var image = new Mat(303, 201, MatType.CV_8UC3, Scalar.Gray)) Cv2.ImWrite(sameSize, image);
        using (var image = new Mat(302, 200, MatType.CV_8UC3, Scalar.Gray)) Cv2.ImWrite(wrongSize, image);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.Irradiance.SelectSkyPhotoForTest(sameSize); await window.InputsSettled;
            Assert.Contains("140° full", window.Irradiance.FieldOfViewTextForTest);
            Assert.Contains("coverage unconfirmed", window.Irradiance.FieldOfViewTextForTest);
            window.Irradiance.SelectSkyPhotoForTest(wrongSize); await window.InputsSettled;
            Assert.Equal("FOV unavailable", window.Irradiance.FieldOfViewTextForTest);
            window.Irradiance.SelectSkyPhotoForTest(fixture.Settings.SkyImage); await window.InputsSettled;
            Assert.Contains("140° full", window.Irradiance.FieldOfViewTextForTest);
            Assert.Contains("usable circular FOV", window.Irradiance.FieldOfViewTextForTest);
            Assert.Null(window.Completed); Assert.False(Directory.Exists(Path.Combine(AppData.Root, "models-v1")));
        }
        finally { window.Close(); }
    });
}
