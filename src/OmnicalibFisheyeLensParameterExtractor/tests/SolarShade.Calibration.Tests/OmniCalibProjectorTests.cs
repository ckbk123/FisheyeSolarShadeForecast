using OpenCvSharp;
using SolarShade.Core.Models;
using SolarShade.Camera;
using Xunit;

namespace SolarShade.Calibration.Tests;

public sealed class OmniCalibProjectorTests
{
    private static readonly SiteConfiguration LevelCamera = new(
        0, 0, 0, 180, 0, 0, 0,
        new DateOnly(2023, 1, 1), new DateOnly(2023, 1, 1), IrradianceSource.NasaPower);

    [Fact]
    public void OpticalAxis_ProjectsToPrincipalPoint()
    {
        var calibration = Profile();
        var valid = OmniCalibProjector.TryProjectSolarDirection(0, 0, calibration, LevelCamera, out var x, out var y);
        Assert.True(valid);
        Assert.Equal(500, x, 8);
        Assert.Equal(500, y, 8);
    }

    [Theory]
    [InlineData(0, 500, 400)]   // north is image top
    [InlineData(90, 400, 500)]  // east is image left
    [InlineData(180, 500, 600)] // south is image bottom
    [InlineData(270, 600, 500)] // west is image right
    public void CardinalDirections_PreserveLegacyImageConvention(double azimuth, double expectedX, double expectedY)
    {
        var valid = OmniCalibProjector.TryProjectSolarDirection(azimuth, 1, Profile(100 / (Math.PI / 180)), LevelCamera, out var x, out var y);
        Assert.True(valid);
        Assert.Equal(expectedX, x, 6);
        Assert.Equal(expectedY, y, 6);
    }

    [Fact]
    public void LegacyYaml_IsImportedAsOmniCalibPolynomial()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"solarshade-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var yaml = Path.Combine(directory, "calibration.yml");
            File.WriteAllText(yaml, "poly_incident_angle_to_radius: [0.0, 500.0, 2.0, 0.0, 0.0]\nprincipal_point: [500.0, 500.0]\nfov: 80.0\n");
            var imagePath = Path.Combine(directory, "reference.png");
            using (var image = Mat.Zeros(1000, 1000, MatType.CV_8UC1).ToMat()) Cv2.ImWrite(imagePath, image);

            var imported = new OmniCalibProfileImporter().ImportLegacyYaml(yaml, imagePath);

            Assert.Equal(CameraModelKind.OmniCalibIncidentAnglePolynomial, imported.CameraModel);
            Assert.Equal([0.0, 500.0, 2.0, 0.0, 0.0], imported.IncidentAngleToRadiusPolynomial);
            Assert.Equal([500.0, 500.0], imported.PrincipalPoint);
            Assert.Equal(80, imported.MaximumIncidentAngleDegrees);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LegacyYaml_WithoutValidatedFov_IsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"solarshade-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var yaml = Path.Combine(directory, "calibration.yml");
            File.WriteAllText(yaml, "poly_incident_angle_to_radius: [0.0, 500.0]\nprincipal_point: [500.0, 500.0]\n");
            var imagePath = Path.Combine(directory, "reference.png");
            using (var image = Mat.Zeros(1000, 1000, MatType.CV_8UC1).ToMat()) Cv2.ImWrite(imagePath, image);

            var error = Assert.Throws<InvalidDataException>(
                () => new OmniCalibProfileImporter().ImportLegacyYaml(yaml, imagePath));

            Assert.Contains("explicit validated 'fov'", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static CalibrationResult Profile(double linearCoefficient = 100) => new(
        2, CameraModelKind.OmniCalibIncidentAnglePolynomial, 1000, 1000,
        [500, 500], [0, linearCoefficient], 89, 499, null, 12, [], DateTimeOffset.UtcNow,
        "test OmniCalib polynomial");
}
