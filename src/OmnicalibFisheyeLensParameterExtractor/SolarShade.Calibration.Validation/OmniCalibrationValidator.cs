using OpenCvSharp;
using SolarShade.Core.Models;

namespace SolarShade.Calibration.Validation;

public sealed class OmniCalibrationValidator
{
    public CalibrationValidationReport Validate(
        CalibrationObservationDocument observations,
        CalibrationReferenceSolution solution,
        string? overlayDirectory = null)
    {
        ValidateShape(observations, solution);
        if (overlayDirectory is not null) Directory.CreateDirectory(overlayDirectory);

        var allDistances = new List<double>();
        var allDx = new List<double>();
        var allDy = new List<double>();
        var perImage = new List<PerImageReprojectionError>();
        var overlayItems = new List<OverlayItem>();

        for (var imageIndex = 0; imageIndex < observations.Images.Count; imageIndex++)
        {
            var image = observations.Images[imageIndex];
            var extrinsic = solution.Extrinsics[imageIndex];
            var projected = new double[observations.ObjectPointsXyz.Length][];
            var distances = new double[projected.Length];

            for (var pointIndex = 0; pointIndex < projected.Length; pointIndex++)
            {
                projected[pointIndex] = Project(
                    observations.ObjectPointsXyz[pointIndex], extrinsic,
                    solution.IncidentAngleToRadiusPolynomial, solution.PrincipalPointXy);
                var dx = projected[pointIndex][0] - image.PointsXy[pointIndex][0];
                var dy = projected[pointIndex][1] - image.PointsXy[pointIndex][1];
                distances[pointIndex] = Math.Sqrt(dx * dx + dy * dy);
                allDx.Add(dx);
                allDy.Add(dy);
                allDistances.Add(distances[pointIndex]);
            }

            var imageMetrics = new PerImageReprojectionError(
                image.Name,
                Math.Sqrt(distances.Select(value => value * value).Average()),
                distances.Average(),
                distances.Max());
            perImage.Add(imageMetrics);
            overlayItems.Add(new OverlayItem(image, projected, distances, imageMetrics));
        }

        var sorted = allDistances.OrderBy(value => value).ToArray();
        var squaredCoordinateSum = allDx.Select(value => value * value).Sum()
                                   + allDy.Select(value => value * value).Sum();
        var summary = new ReprojectionErrorSummary(
            Math.Sqrt(allDistances.Select(value => value * value).Average()),
            Math.Sqrt(squaredCoordinateSum / (2 * allDistances.Count)),
            allDistances.Average(),
            Percentile(sorted, 0.5),
            Percentile(sorted, 0.95),
            allDistances.Max(),
            allDistances.Count);
        var report = new CalibrationValidationReport(
            1, "central incident-angle-to-radius polynomial", summary, perImage);
        if (overlayDirectory is not null)
        {
            foreach (var item in overlayItems)
                WriteOverlay(item, summary, overlayDirectory);
            WriteContactSheet(overlayItems, summary, overlayDirectory);
        }
        return report;
    }

    internal static double[] Project(
        IReadOnlyList<double> world,
        IReadOnlyList<double[]> extrinsic,
        IReadOnlyList<double> polynomial,
        IReadOnlyList<double> principalPoint)
    {
        var x = extrinsic[0][0] * world[0] + extrinsic[0][1] * world[1]
                + extrinsic[0][2] * world[2] + extrinsic[0][3];
        var y = extrinsic[1][0] * world[0] + extrinsic[1][1] * world[1]
                + extrinsic[1][2] * world[2] + extrinsic[1][3];
        var z = extrinsic[2][0] * world[0] + extrinsic[2][1] * world[1]
                + extrinsic[2][2] * world[2] + extrinsic[2][3];
        var norm = Math.Sqrt(x * x + y * y + z * z);
        var radialNorm = Math.Sqrt(x * x + y * y);
        if (norm <= 1e-15 || radialNorm <= 1e-15)
            return [principalPoint[0], principalPoint[1]];

        var theta = Math.Acos(Math.Clamp(z / norm, -1.0, 1.0));
        var radius = EvaluatePolynomial(polynomial, theta);
        return [principalPoint[0] + radius * x / radialNorm, principalPoint[1] + radius * y / radialNorm];
    }

    private static double EvaluatePolynomial(IReadOnlyList<double> coefficients, double value)
    {
        var result = 0.0;
        for (var index = coefficients.Count - 1; index >= 0; index--)
            result = result * value + coefficients[index];
        return result;
    }

    private static void ValidateShape(
        CalibrationObservationDocument observations,
        CalibrationReferenceSolution solution)
    {
        if (observations.CoordinateOrder != "xy_zero_based_after_exif_orientation")
            throw new InvalidDataException($"Unsupported coordinate convention: {observations.CoordinateOrder}");
        if (solution.Extrinsics.Length != observations.Images.Count)
            throw new InvalidDataException("The number of solution extrinsics does not match the observed images.");
        if (solution.PrincipalPointXy.Length != 2 || solution.IncidentAngleToRadiusPolynomial.Length < 2)
            throw new InvalidDataException("The solution has malformed intrinsic parameters.");
        for (var index = 0; index < observations.Images.Count; index++)
        {
            if (observations.Images[index].PointsXy.Length != observations.ObjectPointsXyz.Length)
                throw new InvalidDataException($"Image {observations.Images[index].Name} has the wrong point count.");
            if (solution.Extrinsics[index].Length != 3 || solution.Extrinsics[index].Any(row => row.Length != 4))
                throw new InvalidDataException($"Extrinsic matrix {index} is not 3 x 4.");
        }
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        var position = (sorted.Count - 1) * fraction;
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        if (low == high) return sorted[low];
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }

    private static void WriteOverlay(
        OverlayItem item,
        ReprojectionErrorSummary total,
        string outputDirectory)
    {
        using var image = Cv2.ImRead(item.Observation.Path, ImreadModes.Color);
        if (image.Empty()) return;
        for (var index = 0; index < item.Projected.Count; index++)
        {
            var actual = new Point(
                (int)Math.Round(item.Observation.PointsXy[index][0]),
                (int)Math.Round(item.Observation.PointsXy[index][1]));
            var fitted = new Point(
                (int)Math.Round(item.Projected[index][0]),
                (int)Math.Round(item.Projected[index][1]));
            Cv2.Line(image, actual, fitted, item.Distances[index] <= 2 ? Scalar.Yellow : Scalar.Orange, 2, LineTypes.AntiAlias);
            Cv2.Circle(image, actual, 13, Scalar.LimeGreen, 3, LineTypes.AntiAlias);
            Cv2.DrawMarker(image, fitted, Scalar.Red, MarkerTypes.Cross, 22, 3, LineTypes.AntiAlias);
        }

        Cv2.Rectangle(image, new Rect(0, 0, image.Width, 155), Scalar.Black, -1);
        Cv2.PutText(image,
            $"{item.Observation.Name}   Image RMS: {item.Metrics.Rmse2dPixels:F3} px   Mean: {item.Metrics.MeanEuclideanPixels:F3} px   Max: {item.Metrics.MaxEuclideanPixels:F3} px",
            new Point(35, 55), HersheyFonts.HersheySimplex, 1.15, Scalar.White, 2, LineTypes.AntiAlias);
        Cv2.PutText(image,
            $"TOTAL RMS: {total.Rmse2dPixels:F3} px   Mean: {total.MeanEuclideanPixels:F3} px   P95: {total.P95EuclideanPixels:F3} px   Points: {total.PointCount}",
            new Point(35, 105), HersheyFonts.HersheySimplex, 1.05, Scalar.White, 2, LineTypes.AntiAlias);
        Cv2.PutText(image, "GREEN CIRCLE = detected corner    RED CROSS = calibrated reprojection    LINE = residual",
            new Point(35, 145), HersheyFonts.HersheySimplex, 0.8, Scalar.Yellow, 2, LineTypes.AntiAlias);

        Cv2.ImWrite(Path.Combine(outputDirectory, $"overlay_{Path.GetFileNameWithoutExtension(item.Observation.Name)}.jpg"), image,
            [new ImageEncodingParam(ImwriteFlags.JpegQuality, 94)]);
    }

    private static void WriteContactSheet(
        IReadOnlyList<OverlayItem> items,
        ReprojectionErrorSummary total,
        string outputDirectory)
    {
        const int columns = 3;
        const int thumbnailWidth = 450;
        const int thumbnailHeight = 600;
        const int headerHeight = 120;
        var rows = (items.Count + columns - 1) / columns;
        using var sheet = new Mat(headerHeight + rows * thumbnailHeight, columns * thumbnailWidth,
            MatType.CV_8UC3, Scalar.Black);
        Cv2.PutText(sheet, "FISHEYE CALIBRATION REVIEW",
            new Point(25, 42), HersheyFonts.HersheySimplex, 1.15, Scalar.White, 2, LineTypes.AntiAlias);
        Cv2.PutText(sheet,
            $"Total 2-D RMS {total.Rmse2dPixels:F3} px | mean {total.MeanEuclideanPixels:F3} px | P95 {total.P95EuclideanPixels:F3} px | max {total.MaxEuclideanPixels:F3} px | {total.PointCount} points",
            new Point(25, 88), HersheyFonts.HersheySimplex, 0.72, Scalar.Yellow, 2, LineTypes.AntiAlias);

        for (var index = 0; index < items.Count; index++)
        {
            var path = Path.Combine(outputDirectory,
                $"overlay_{Path.GetFileNameWithoutExtension(items[index].Observation.Name)}.jpg");
            using var source = Cv2.ImRead(path, ImreadModes.Color);
            if (source.Empty()) continue;
            using var thumbnail = new Mat();
            Cv2.Resize(source, thumbnail, new Size(thumbnailWidth, thumbnailHeight), interpolation: InterpolationFlags.Area);
            var column = index % columns;
            var row = index / columns;
            using var region = new Mat(sheet,
                new Rect(column * thumbnailWidth, headerHeight + row * thumbnailHeight, thumbnailWidth, thumbnailHeight));
            thumbnail.CopyTo(region);
        }

        Cv2.ImWrite(Path.Combine(outputDirectory, "contact-sheet.jpg"), sheet,
            [new ImageEncodingParam(ImwriteFlags.JpegQuality, 94)]);
    }

    private sealed record OverlayItem(
        CalibrationImageObservation Observation,
        IReadOnlyList<double[]> Projected,
        IReadOnlyList<double> Distances,
        PerImageReprojectionError Metrics);
}
