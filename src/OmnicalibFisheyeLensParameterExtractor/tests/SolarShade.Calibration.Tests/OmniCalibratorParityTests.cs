using System.Text.Json;
using SolarShade.Core.Models;
using SolarShade.Calibration.Solver;
using SolarShade.Calibration.Validation;
using Xunit;

namespace SolarShade.Calibration.Tests;

public sealed class OmniCalibratorParityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Solve_MatchesFrozenPythonReference()
    {
        var root = FindRepositoryRoot();
        var observations = Read<CalibrationObservationDocument>(Path.Combine(
            root, "artifacts", "calibration-validation", "ponitz-22mm", "observations.json"));
        var python = Read<CalibrationReferenceSolution>(Path.Combine(
            root, "artifacts", "calibration-validation", "ponitz-22mm", "result.json"));

        var native = new OmniCalibrator().Solve(observations);
        var nativeReference = native.ToReferenceSolution(observations);
        var validator = new OmniCalibrationValidator();
        var pythonReport = validator.Validate(observations, python);
        var nativeReport = validator.Validate(observations, nativeReference);

        Assert.InRange(Math.Abs(nativeReport.Metrics.MeanEuclideanPixels -
                                pythonReport.Metrics.MeanEuclideanPixels), 0, 0.05);
        Assert.InRange(nativeReport.Metrics.Rmse2dPixels - pythonReport.Metrics.Rmse2dPixels,
            double.NegativeInfinity, 0.05);
        Assert.InRange(Distance(native.PrincipalPointXy, python.PrincipalPointXy), 0, 0.01);

        var maximumRadialDifference = Enumerable.Range(0, 101).Max(index =>
        {
            var theta = 66.43 * index / 100 * Math.PI / 180;
            return Math.Abs(Evaluate(native.IncidentAngleToRadiusPolynomial, theta) -
                            Evaluate(python.IncidentAngleToRadiusPolynomial, theta));
        });
        Assert.InRange(maximumRadialDifference, 0, 0.01);
        Assert.All(native.Rotations, rotation =>
        {
            Assert.InRange(Math.Abs(Determinant(rotation) - 1), 0, 1e-9);
            Assert.InRange(OrthonormalError(rotation), 0, 1e-9);
        });
    }

[Fact]
    public void Solve_RecoversSyntheticCentralPolynomialCamera()
    {
        const int columns = 6;
        const int rows = 9;
        var world = (from row in Enumerable.Range(0, rows)
                     from column in Enumerable.Range(0, columns)
                     select new[] { column * 20.0, row * 20.0, 0.0 }).ToArray();
        var principal = new[] { 500.0, 500.0 };
        var polynomial = new[] { 0.0, 700.0, 18.0, -7.0, 1.5 };
        var poseParameters = new[]
        {
            (-0.20, -0.15, -40.0, -70.0, 420.0), (0.18, -0.10, -55.0, -85.0, 460.0),
            (-0.12, 0.22, -35.0, -75.0, 440.0), (0.15, 0.18, -60.0, -65.0, 480.0),
            (-0.28, 0.08, -30.0, -90.0, 455.0), (0.25, -0.20, -65.0, -80.0, 470.0),
            (0.08, 0.28, -45.0, -60.0, 430.0), (-0.10, -0.25, -50.0, -95.0, 490.0)
        };
        var images = poseParameters.Select((pose, index) =>
        {
            var rotation = Rotation(pose.Item1, pose.Item2);
            var translation = new[] { pose.Item3, pose.Item4, pose.Item5 };
            var points = world.Select(point => Project(point, rotation, translation, polynomial, principal)).ToArray();
            return new CalibrationImageObservation($"synthetic-{index}.png", $"synthetic-{index}.png", "", points);
        }).ToArray();
        var observations = new CalibrationObservationDocument(
            1, "xy_zero_based_after_exif_orientation", [1001, 1001], [columns, rows], 20,
            new CalibrationDetectorMetadata("synthetic", 1, 0, "synthetic"), images, world);

        var solution = new OmniCalibrator().Solve(observations, new OmniCalibrationOptions(
            PrincipalPointSearchStepPixels: 0,
            PrincipalPointSearchRadiusPixels: 0));
        var report = new OmniCalibrationValidator().Validate(observations, solution.ToReferenceSolution(observations));

        Assert.InRange(report.Metrics.Rmse2dPixels, 0, 1e-5);
        Assert.InRange(Distance(solution.PrincipalPointXy, principal), 0, 1e-3);
        var maximumRadialDifference = Enumerable.Range(0, 101).Max(index =>
        {
            var theta = 0.7 * index / 100;
            return Math.Abs(Evaluate(solution.IncidentAngleToRadiusPolynomial, theta) - Evaluate(polynomial, theta));
        });
        Assert.InRange(maximumRadialDifference, 0, 1e-3);
    }

    [Fact]
    public void Solve_ScalesOnlyTranslationsWhenSquareSizeChanges()
    {
        var root = FindRepositoryRoot();
        var observations22 = Read<CalibrationObservationDocument>(Path.Combine(
            root, "artifacts", "calibration-validation", "ponitz-22mm", "observations.json"));
        var observations33 = Read<CalibrationObservationDocument>(Path.Combine(
            root, "artifacts", "calibration-validation", "ponitz-33mm", "observations.json"));
        var calibrator = new OmniCalibrator();
        var solution22 = calibrator.Solve(observations22);
        var solution33 = calibrator.Solve(observations33);

        Assert.InRange(Distance(solution22.PrincipalPointXy, solution33.PrincipalPointXy), 0, 1e-8);
        for (var index = 0; index < solution22.IncidentAngleToRadiusPolynomial.Length; index++)
            Assert.InRange(Math.Abs(solution22.IncidentAngleToRadiusPolynomial[index] -
                                    solution33.IncidentAngleToRadiusPolynomial[index]), 0, 1e-7);
        for (var image = 0; image < solution22.Translations.Length; image++)
            for (var axis = 0; axis < 3; axis++)
                Assert.InRange(Math.Abs(solution33.Translations[image][axis] /
                                        solution22.Translations[image][axis] - 1.5), 0, 1e-10);
    }

    [Fact]
    public void FastDetectionDefault_UsesAvailableLogicalProcessorsUpToTwelve()
    {
        var settings = CheckerboardDetectionSettings.CreateFastDefault();

        Assert.Equal(Math.Clamp(Environment.ProcessorCount, 1, 12), settings.ImageWorkerCount);
        Assert.Equal(1, settings.OpenCvThreadCount);
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)!;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolarShade.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static double Distance(IReadOnlyList<double> a, IReadOnlyList<double> b) =>
        Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2));

    private static double Evaluate(IReadOnlyList<double> coefficients, double value)
    {
        var result = 0.0;
        for (var index = coefficients.Count - 1; index >= 0; index--) result = result * value + coefficients[index];
        return result;
    }

    private static double Determinant(double[][] r) =>
        r[0][0] * (r[1][1] * r[2][2] - r[1][2] * r[2][1]) -
        r[0][1] * (r[1][0] * r[2][2] - r[1][2] * r[2][0]) +
        r[0][2] * (r[1][0] * r[2][1] - r[1][1] * r[2][0]);

    private static double OrthonormalError(double[][] r)
    {
        var sum = 0.0;
        for (var row = 0; row < 3; row++)
            for (var column = 0; column < 3; column++)
            {
                var dot = Enumerable.Range(0, 3).Sum(index => r[index][row] * r[index][column]);
                var expected = row == column ? 1.0 : 0.0;
                sum += Math.Pow(dot - expected, 2);
            }
        return Math.Sqrt(sum);
    }

    private static double[][] Rotation(double xAngle, double yAngle)
    {
        var cx = Math.Cos(xAngle); var sx = Math.Sin(xAngle);
        var cy = Math.Cos(yAngle); var sy = Math.Sin(yAngle);
        return
        [
            [cy, sy * sx, sy * cx],
            [0, cx, -sx],
            [-sy, cy * sx, cy * cx]
        ];
    }

    private static double[] Project(
        double[] point,
        double[][] rotation,
        double[] translation,
        double[] polynomial,
        double[] principal)
    {
        var view = Enumerable.Range(0, 3).Select(row => translation[row] +
            Enumerable.Range(0, 3).Sum(column => rotation[row][column] * point[column])).ToArray();
        var planar = Math.Sqrt(view[0] * view[0] + view[1] * view[1]);
        var theta = Math.Atan2(planar, view[2]);
        var radius = Evaluate(polynomial, theta);
        return [principal[0] + view[0] / planar * radius, principal[1] + view[1] / planar * radius];
    }
}

