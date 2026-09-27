using System.Text.Json.Serialization;

namespace SolarShade.Core.Models;

public enum CheckerboardSearchMode { ClassicFirst, SectorFirst, SectorFastFirst }

public sealed record CheckerboardDetectionSettings(
    int InnerColumns = 6,
    int InnerRows = 9,
    double SquareSizeMillimetres = 22.0,
    int DownsampleFactor = 8,
    double SubpixelEpsilon = 0.001,
    int OpenCvThreadCount = 1,
    int ImageWorkerCount = 1,
    CheckerboardSearchMode SearchMode = CheckerboardSearchMode.SectorFastFirst,
    bool ReadImageOnce = true,
    bool SharpenFullResolution = true)
{
    public static CheckerboardDetectionSettings CreateFastDefault()
    {
        // Use physical work queues rather than relying on nested OpenCV
        // parallel regions. There can be at most one worker per image in the
        // current 12-photo calibration set; the cap controls peak image memory.
        var workers = Math.Clamp(Environment.ProcessorCount, 1, 12);
        return new CheckerboardDetectionSettings(OpenCvThreadCount: 1, ImageWorkerCount: workers);
    }

    public static CheckerboardDetectionSettings CreateVerificationDefault() => new(
        SearchMode: CheckerboardSearchMode.ClassicFirst, ReadImageOnce: false);
}

public sealed record CalibrationDetectorMetadata(
    [property: JsonPropertyName("opencv_version")] string OpenCvVersion,
    [property: JsonPropertyName("downsample")] int Downsample,
    [property: JsonPropertyName("corner_subpix_epsilon")] double CornerSubpixelEpsilon,
    [property: JsonPropertyName("sequence")] string Sequence,
    [property: JsonPropertyName("opencv_threads")] int OpenCvThreads = 1,
    [property: JsonPropertyName("image_workers")] int ImageWorkers = 1);

public sealed record CalibrationImageObservation(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("points_xy")] double[][] PointsXy);

public sealed record CalibrationObservationDocument(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("coordinate_order")] string CoordinateOrder,
    [property: JsonPropertyName("image_size_width_height")] int[] ImageSizeWidthHeight,
    [property: JsonPropertyName("inner_corners_columns_rows")] int[] InnerCornersColumnsRows,
    [property: JsonPropertyName("square_size_mm")] double SquareSizeMillimetres,
    [property: JsonPropertyName("detector")] CalibrationDetectorMetadata Detector,
    [property: JsonPropertyName("images")] IReadOnlyList<CalibrationImageObservation> Images,
    [property: JsonPropertyName("object_points_xyz")] double[][] ObjectPointsXyz);

public sealed record CalibrationReferenceSolution(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("image_size_width_height")] int[] ImageSizeWidthHeight,
    [property: JsonPropertyName("square_size_mm")] double SquareSizeMillimetres,
    [property: JsonPropertyName("inner_corners_columns_rows")] int[] InnerCornersColumnsRows,
    [property: JsonPropertyName("principal_point_xy")] double[] PrincipalPointXy,
    [property: JsonPropertyName("polynomial_incident_angle_to_radius")] double[] IncidentAngleToRadiusPolynomial,
    [property: JsonPropertyName("polynomial_radius_to_z")] double[] RadiusToZPolynomial,
    [property: JsonPropertyName("extrinsics")] double[][][] Extrinsics);

public sealed record ReprojectionErrorSummary(
    [property: JsonPropertyName("rmse_2d_px")] double Rmse2dPixels,
    [property: JsonPropertyName("rmse_per_coordinate_px")] double RmsePerCoordinatePixels,
    [property: JsonPropertyName("mean_euclidean_px")] double MeanEuclideanPixels,
    [property: JsonPropertyName("median_euclidean_px")] double MedianEuclideanPixels,
    [property: JsonPropertyName("p95_euclidean_px")] double P95EuclideanPixels,
    [property: JsonPropertyName("max_euclidean_px")] double MaxEuclideanPixels,
    [property: JsonPropertyName("point_count")] int PointCount);

public sealed record PerImageReprojectionError(
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("rmse_2d_px")] double Rmse2dPixels,
    [property: JsonPropertyName("mean_euclidean_px")] double MeanEuclideanPixels,
    [property: JsonPropertyName("max_euclidean_px")] double MaxEuclideanPixels);

public sealed record CalibrationValidationReport(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("metrics")] ReprojectionErrorSummary Metrics,
    [property: JsonPropertyName("per_image")] IReadOnlyList<PerImageReprojectionError> PerImage);
