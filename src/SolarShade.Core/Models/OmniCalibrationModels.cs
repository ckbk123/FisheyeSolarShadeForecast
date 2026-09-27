using System.Text.Json.Serialization;

namespace SolarShade.Core.Models;

/// <summary>Auto selects wide SIMD when supported, otherwise the original MathNet implementation.
/// Scalar forces an unvectorized comparison kernel; MathNet forces the original implementation.</summary>
public enum OmniSolverKernel { Auto, Scalar, MathNet }

public sealed record OmniCalibrationOptions(
    int PolynomialDegree = 4,
    double InitializationMeanErrorThresholdPixels = 100.0,
    int? MinimumInitializationImages = null,
    int PrincipalPointSearchStepPixels = 10,
    int PrincipalPointSearchRadiusPixels = 100,
    int MaximumIterations = 250,
    int MaximumDegreeOfParallelism = 0,
    double FunctionTolerance = 1e-10,
    double StepTolerance = 1e-10,
    double GradientTolerance = 1e-10,
    OmniSolverKernel Kernel = OmniSolverKernel.Auto);

public sealed record OmniCalibrationDiagnostics(
    [property: JsonPropertyName("converged")] bool Converged,
    [property: JsonPropertyName("termination_reason")] string TerminationReason,
    [property: JsonPropertyName("iterations")] int Iterations,
    [property: JsonPropertyName("accepted_steps")] int AcceptedSteps,
    [property: JsonPropertyName("function_evaluations")] int FunctionEvaluations,
    [property: JsonPropertyName("initial_cost")] double InitialCost,
    [property: JsonPropertyName("final_cost")] double FinalCost);

public sealed record OmniCalibrationSolution(
    double[][][] Rotations,
    double[][] Translations,
    double[] IncidentAngleToRadiusPolynomial,
    double[] RadiusToZPolynomial,
    double[] PrincipalPointXy,
    bool[] InitializationImageMask,
    OmniCalibrationDiagnostics SubsetOptimization,
    OmniCalibrationDiagnostics FinalOptimization)
{
    public CalibrationReferenceSolution ToReferenceSolution(CalibrationObservationDocument observations)
    {
        var extrinsics = Rotations.Select((rotation, index) =>
            Enumerable.Range(0, 3).Select(row =>
                new[] { rotation[row][0], rotation[row][1], rotation[row][2], Translations[index][row] })
                .ToArray()).ToArray();

        return new CalibrationReferenceSolution(
            1,
            "SolarShade native C# port of Thomas Poenitz py-omnicalib (MIT)",
            observations.ImageSizeWidthHeight,
            observations.SquareSizeMillimetres,
            observations.InnerCornersColumnsRows,
            PrincipalPointXy,
            IncidentAngleToRadiusPolynomial,
            RadiusToZPolynomial,
            extrinsics);
    }

    public CalibrationResult ToCalibrationResult(
        CalibrationObservationDocument observations,
        ReprojectionErrorSummary metrics)
    {
        var maximumAngle = 0.0;
        for (var imageIndex = 0; imageIndex < Rotations.Length; imageIndex++)
            foreach (var point in observations.ObjectPointsXyz)
            {
                var view = new double[3];
                for (var row = 0; row < 3; row++)
                    view[row] = Translations[imageIndex][row] + Enumerable.Range(0, 3)
                        .Sum(column => Rotations[imageIndex][row][column] * point[column]);
                var planar = Math.Sqrt(view[0] * view[0] + view[1] * view[1]);
                maximumAngle = Math.Max(maximumAngle, Math.Atan2(planar, view[2]) * 180 / Math.PI);
            }

        var radius = 0.0;
        var theta = maximumAngle * Math.PI / 180;
        for (var index = IncidentAngleToRadiusPolynomial.Length - 1; index >= 0; index--)
            radius = radius * theta + IncidentAngleToRadiusPolynomial[index];
        var edgeRadius = new[]
        {
            PrincipalPointXy[0], observations.ImageSizeWidthHeight[0] - PrincipalPointXy[0],
            PrincipalPointXy[1], observations.ImageSizeWidthHeight[1] - PrincipalPointXy[1]
        }.Min();

        return new CalibrationResult(
            2,
            CameraModelKind.OmniCalibIncidentAnglePolynomial,
            observations.ImageSizeWidthHeight[0],
            observations.ImageSizeWidthHeight[1],
            PrincipalPointXy.ToArray(),
            IncidentAngleToRadiusPolynomial.ToArray(),
            maximumAngle,
            Math.Min(edgeRadius, radius),
            metrics.Rmse2dPixels,
            observations.Images.Count,
            [],
            DateTimeOffset.UtcNow,
            "Native C# py-omnicalib port: linear initialization + SE(3) Levenberg-Marquardt");
    }
}

public sealed record NativeCalibrationResultDocument(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("image_size_width_height")] int[] ImageSizeWidthHeight,
    [property: JsonPropertyName("square_size_mm")] double SquareSizeMillimetres,
    [property: JsonPropertyName("inner_corners_columns_rows")] int[] InnerCornersColumnsRows,
    [property: JsonPropertyName("principal_point_xy")] double[] PrincipalPointXy,
    [property: JsonPropertyName("polynomial_incident_angle_to_radius")] double[] IncidentAngleToRadiusPolynomial,
    [property: JsonPropertyName("polynomial_radius_to_z")] double[] RadiusToZPolynomial,
    [property: JsonPropertyName("extrinsics")] double[][][] Extrinsics,
    [property: JsonPropertyName("initialization_image_mask")] bool[] InitializationImageMask,
    [property: JsonPropertyName("subset_optimization")] OmniCalibrationDiagnostics SubsetOptimization,
    [property: JsonPropertyName("final_optimization")] OmniCalibrationDiagnostics FinalOptimization,
    [property: JsonPropertyName("solve_time_ms")] double SolveTimeMilliseconds,
    [property: JsonPropertyName("metrics")] ReprojectionErrorSummary Metrics,
    [property: JsonPropertyName("per_image")] IReadOnlyList<PerImageReprojectionError> PerImage);

