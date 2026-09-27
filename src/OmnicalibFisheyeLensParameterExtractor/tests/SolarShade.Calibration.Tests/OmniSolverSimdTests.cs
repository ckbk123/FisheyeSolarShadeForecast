using MathNet.Numerics.LinearAlgebra;
using SolarShade.Calibration.Solver;
using SolarShade.Calibration.Validation;
using SolarShade.Core.Models;
using System.Text.Json;
using Xunit;

namespace SolarShade.Calibration.Tests;

public sealed class OmniSolverSimdTests
{
    [Fact]
    public void Dot_HandlesOffsetsAndEveryVectorTail()
    {
        var left = Enumerable.Range(0, 140).Select(i => Math.Sin(i) * 100).ToArray();
        var right = Enumerable.Range(0, 140).Select(i => Math.Cos(i * 0.3)).ToArray();
        for (var count = 0; count <= 129; count++)
        {
            var expected = Enumerable.Range(0, count).Sum(i => left[i + 3] * right[i + 7]);
            Near(expected, OmniCalibrator.Dot(left, 3, right, 7, count, false));
            Near(expected, OmniCalibrator.Dot(left, 3, right, 7, count, true));
        }
    }

    [Fact]
    public void Dot_RejectsInvalidSlicesBeforeVectorLoads()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OmniCalibrator.Dot(new double[8], 0, new double[7], 0, 8, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => OmniCalibrator.Dot(new double[8], -1, new double[8], 0, 4, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => OmniCalibrator.Dot(new double[8], 0, new double[8], 0, -1, true));
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(109, 14)]
    [InlineData(1296, 78)]
    public void NormalEquations_MatchIndependentMathNetProducts(int rows, int columns)
    {
        var random = new Random(789);
        var j = Matrix<double>.Build.Dense(rows, columns, (_, _) => (random.NextDouble() - 0.5) * 100);
        var r = Vector<double>.Build.Dense(rows, _ => random.NextDouble() - 0.5);
        var expectedNormal = j.TransposeThisAndMultiply(j);
        var expectedGradient = j.TransposeThisAndMultiply(r);
        foreach (var mode in new[] { OmniSolverKernel.Scalar, OmniSolverKernel.Auto })
        {
            var (normal, gradient) = OmniCalibrator.BuildNormalEquations(j, r, mode);
            for (var a = 0; a < columns; a++)
            {
                Near(expectedGradient[a], gradient[a]);
                for (var b = 0; b < columns; b++)
                {
                    Near(expectedNormal[a, b], normal[a, b]);
                    Assert.Equal(normal[a, b], normal[b, a]);
                }
            }
        }
    }

    [Fact]
    public void AllKernels_PreserveFittedCameraAndResiduals()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SolarShade.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "artifacts", "calibration-validation", "ponitz-22mm", "observations.json");
        var observations = JsonSerializer.Deserialize<CalibrationObservationDocument>(File.ReadAllText(path))!;
        var solver = new OmniCalibrator();
        var reference = solver.Solve(observations, new OmniCalibrationOptions(Kernel: OmniSolverKernel.MathNet));
        var validator = new OmniCalibrationValidator();
        var expected = validator.Validate(observations, reference.ToReferenceSolution(observations));
        foreach (var mode in new[] { OmniSolverKernel.Scalar, OmniSolverKernel.Auto })
        {
            var solution = solver.Solve(observations, new OmniCalibrationOptions(Kernel: mode));
            var report = validator.Validate(observations, solution.ToReferenceSolution(observations));
            Assert.True(solution.FinalOptimization.Converged);
            Assert.InRange(Math.Abs(report.Metrics.Rmse2dPixels - expected.Metrics.Rmse2dPixels), 0, 1e-9);
            for (var axis = 0; axis < 2; axis++)
                Assert.InRange(Math.Abs(solution.PrincipalPointXy[axis] - reference.PrincipalPointXy[axis]), 0, 1e-4);
            for (var sample = 0; sample <= 100; sample++)
            {
                var theta = sample / 100.0 * 66.43 * Math.PI / 180;
                var a = solution.IncidentAngleToRadiusPolynomial.Reverse().Aggregate(0.0, (v, c) => v * theta + c);
                var b = reference.IncidentAngleToRadiusPolynomial.Reverse().Aggregate(0.0, (v, c) => v * theta + c);
                Assert.InRange(Math.Abs(a - b), 0, 1e-4);
            }
        }
    }

    private static void Near(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, 1e-9 + Math.Abs(expected) * 1e-12);
}
