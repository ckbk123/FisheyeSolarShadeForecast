using SolarShade.Core.Models;
using SolarShade.Calibration.Validation;
using Xunit;

namespace SolarShade.Calibration.Tests;

public sealed class OmniCalibrationValidatorTests
{
    [Fact]
    public void DetectionProfiles_KeepFastModeBoundedAndVerificationModeSerial()
    {
        var fast = CheckerboardDetectionSettings.CreateFastDefault();
        var verification = CheckerboardDetectionSettings.CreateVerificationDefault();

        Assert.InRange(fast.ImageWorkerCount, 1, 12);
        Assert.Equal(1, fast.OpenCvThreadCount);
        Assert.Equal(1, verification.ImageWorkerCount);
        Assert.Equal(1, verification.OpenCvThreadCount);
    }

    [Fact]
    public void Validate_ReportsTrueVectorAndCoordinateRmsSeparately()
    {
        double[][] world = [[0, 0, 0], [10, 0, 0], [0, 10, 0], [10, 10, 0]];
        double[][] extrinsic = [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 100]];
        double[] polynomial = [0, 100];
        double[] principal = [500, 500];
        var residuals = new[] { new[] { 0.0, 0.0 }, new[] { 3.0, 4.0 }, new[] { -3.0, 4.0 }, new[] { 0.0, -10.0 } };
        var observed = world.Select((point, index) =>
        {
            var projected = Project(point, extrinsic, polynomial, principal);
            return new[] { projected[0] - residuals[index][0], projected[1] - residuals[index][1] };
        }).ToArray();
        var document = new CalibrationObservationDocument(
            1, "xy_zero_based_after_exif_orientation", [1000, 1000], [2, 2], 22,
            new CalibrationDetectorMetadata("test", 1, .001, "synthetic"),
            [new CalibrationImageObservation("unused.png", "synthetic", "", observed)], world);
        var solution = new CalibrationReferenceSolution(
            1, "synthetic", [1000, 1000], 22, [2, 2], principal, polynomial, [], [extrinsic]);

        var report = new OmniCalibrationValidator().Validate(document, solution);

        Assert.Equal(Math.Sqrt(37.5), report.Metrics.Rmse2dPixels, 12);
        Assert.Equal(Math.Sqrt(18.75), report.Metrics.RmsePerCoordinatePixels, 12);
        Assert.Equal(5, report.Metrics.MeanEuclideanPixels, 12);
        Assert.Equal(5, report.Metrics.MedianEuclideanPixels, 12);
        Assert.Equal(9.25, report.Metrics.P95EuclideanPixels, 12);
        Assert.Equal(10, report.Metrics.MaxEuclideanPixels, 12);
    }

    [Fact]
    public void Validate_RejectsMismatchedExtrinsicCount()
    {
        var document = new CalibrationObservationDocument(
            1, "xy_zero_based_after_exif_orientation", [10, 10], [2, 2], 22,
            new CalibrationDetectorMetadata("test", 1, .001, "synthetic"),
            [new CalibrationImageObservation("unused", "one", "", [[0, 0]])], [[0, 0, 0]]);
        var solution = new CalibrationReferenceSolution(
            1, "synthetic", [10, 10], 22, [2, 2], [5, 5], [0, 1], [], []);

        Assert.Throws<InvalidDataException>(() => new OmniCalibrationValidator().Validate(document, solution));
    }

    private static double[] Project(
        IReadOnlyList<double> world,
        IReadOnlyList<double[]> extrinsic,
        IReadOnlyList<double> polynomial,
        IReadOnlyList<double> principal)
    {
        var x = extrinsic[0][0] * world[0] + extrinsic[0][3];
        var y = extrinsic[1][1] * world[1] + extrinsic[1][3];
        var z = extrinsic[2][2] * world[2] + extrinsic[2][3];
        var radial = Math.Sqrt(x * x + y * y);
        if (radial == 0) return [principal[0], principal[1]];
        var theta = Math.Atan2(radial, z);
        var radius = polynomial[0] + polynomial[1] * theta;
        return [principal[0] + radius * x / radial, principal[1] + radius * y / radial];
    }
}
