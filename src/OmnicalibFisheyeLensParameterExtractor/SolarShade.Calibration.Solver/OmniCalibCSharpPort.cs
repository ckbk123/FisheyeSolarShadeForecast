using System.Runtime.InteropServices;
using MathNet.Numerics.LinearAlgebra;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using SolarShade.Core.Models;
using SimdDouble = System.Numerics.Vector<double>;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SolarShade.Calibration.Tests")]

namespace SolarShade.Calibration.Solver;

/// <summary>Production image-to-calibration pipeline. No validation/oracle dependency.
/// Polynomials are ascending, angles in radians, points in oriented zero-based x/y pixels.
/// Debug images show observed circles and fitted crosses; CSV retains full precision.</summary>
public sealed class OmniCalibCSharpPort
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static readonly object OpenCvExecutionLock = new();

    public OmniCalibRun Calibrate(string imageDirectory, string outputDirectory,
        CheckerboardDetectionSettings? detection = null, OmniCalibrationOptions? solver = null,
        int previewMaxDimension = 1000)
    {
        // Includes contention in the caller's end-to-end time. OpenCV thread settings are global.
        var total = Stopwatch.StartNew();
        lock (OpenCvExecutionLock)
        {
            var run = CalibrateCore(imageDirectory, outputDirectory, detection, solver, previewMaxDimension);
            return run with { TotalMilliseconds = total.Elapsed.TotalMilliseconds };
        }
    }

    private static OmniCalibRun CalibrateCore(string imageDirectory, string outputDirectory,
        CheckerboardDetectionSettings? detection, OmniCalibrationOptions? solver, int previewMaxDimension)
    {
        var total = Stopwatch.StartNew();
        if (previewMaxDimension < 64) throw new ArgumentOutOfRangeException(nameof(previewMaxDimension));
        detection ??= CheckerboardDetectionSettings.CreateFastDefault();
        detection = detection with { ImageWorkerCount = Math.Min(detection.ImageWorkerCount,
            PortHardwarePolicy.RecommendWorkers(Environment.ProcessorCount, PortHardwarePolicy.GetMemoryBudget())) };
        var previews = new ConcurrentDictionary<string, Mat>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var stage = Stopwatch.StartNew();
            var observations = new PortCheckerboardExtractor().Extract(imageDirectory, detection, (path, gray) =>
            {
                var scale = Math.Min(1.0, (double)previewMaxDimension / Math.Max(gray.Width, gray.Height));
                var preview = new Mat();
                Cv2.Resize(gray, preview, new Size(Math.Max(1, (int)Math.Round(gray.Width * scale)),
                    Math.Max(1, (int)Math.Round(gray.Height * scale))), interpolation: InterpolationFlags.Area);
                previews[path] = preview;
            });
            var detectMs = stage.Elapsed.TotalMilliseconds;
            stage.Restart();
            var solution = new OmniCalibrator().Solve(observations, solver);
            var solveMs = stage.Elapsed.TotalMilliseconds;
            stage.Restart();
            var output = Path.GetFullPath(outputDirectory);
            var debug = Path.Combine(output, "debug");
            Directory.CreateDirectory(debug);
            var reference = solution.ToReferenceSolution(observations);
            WriteYaml(Path.Combine(output, "calibration.yml"), reference);
            File.WriteAllText(Path.Combine(output, "observations.json"), JsonSerializer.Serialize(observations, JsonOptions));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(reference, JsonOptions));
            File.WriteAllText(Path.Combine(output, "solver-diagnostics.json"), JsonSerializer.Serialize(new {
                kernel = OmniCalibrator.GetKernelDescription(solver?.Kernel ?? OmniSolverKernel.Auto),
                solution.InitializationImageMask, solution.SubsetOptimization, solution.FinalOptimization
            }, JsonOptions));
            Parallel.For(0, observations.Images.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(detection.ImageWorkerCount, observations.Images.Count)
            }, i => WriteDebug(debug, i, observations, reference, previews[observations.Images[i].Path]));
            var writeMs = stage.Elapsed.TotalMilliseconds;
            // The measured pipeline completes all calibration and debug file writes before return.
            return new OmniCalibRun(observations, solution, Path.Combine(output, "calibration.yml"), debug,
                detectMs, solveMs, writeMs, total.Elapsed.TotalMilliseconds);
        }
        finally
        {
            foreach (var preview in previews.Values) preview.Dispose();
        }
    }

    public CalibrationObservationDocument Detect(string imageDirectory, CheckerboardDetectionSettings settings) =>
        new PortCheckerboardExtractor().Extract(imageDirectory, settings);

    /// <summary>Profiles per-image work. Image times overlap across workers; use WallMilliseconds for latency.</summary>
    public PortDetectionProfile ProfileDetection(string imageDirectory, CheckerboardDetectionSettings settings)
    {
        var timings = new ConcurrentBag<CalibrationImageTiming>();
        var watch = Stopwatch.StartNew();
        var observations = new PortCheckerboardExtractor().Extract(imageDirectory, settings, (path, gray) =>
        {
            using var preview = new Mat();
            var scale = Math.Min(1.0, 1000.0 / Math.Max(gray.Width, gray.Height));
            Cv2.Resize(gray, preview, new Size((int)Math.Round(gray.Width * scale), (int)Math.Round(gray.Height * scale)),
                interpolation: InterpolationFlags.Area);
        }, timings.Add);
        return new PortDetectionProfile(observations, watch.Elapsed.TotalMilliseconds,
            timings.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>Writes py-omnicalib key names without declaring an unvalidated physical FOV.</summary>
    public static void WriteYaml(string path, CalibrationReferenceSolution solution)
    {
        static string Numbers(IEnumerable<double> values) => "[" + string.Join(", ", values.Select(v =>
            double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) :
            throw new InvalidDataException("Non-finite calibration coefficient."))) + "]";
        var yaml = new StringBuilder("# py-omnicalib conventions; ascending coefficients; radians; oriented zero-based xy\n");
        yaml.AppendLine("principal_point: " + Numbers(solution.PrincipalPointXy));
        yaml.AppendLine("poly_incident_angle_to_radius: " + Numbers(solution.IncidentAngleToRadiusPolynomial));
        yaml.AppendLine("poly_radius_to_z: " + Numbers(solution.RadiusToZPolynomial));
        yaml.AppendLine("image_size: " + Numbers(solution.ImageSizeWidthHeight.Select(v => (double)v)));
        yaml.AppendLine("extrinsics:");
        foreach (var pose in solution.Extrinsics)
        {
            yaml.AppendLine("  - - " + Numbers(pose[0]));
            yaml.AppendLine("    - " + Numbers(pose[1]));
            yaml.AppendLine("    - " + Numbers(pose[2]));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, yaml.ToString(), new UTF8Encoding(false));
    }

    private static void WriteDebug(string directory, int index, CalibrationObservationDocument observations,
        CalibrationReferenceSolution solution, Mat preview)
    {
        var source = observations.Images[index];
        using var overlay = new Mat();
        Cv2.CvtColor(preview, overlay, ColorConversionCodes.GRAY2BGR);
        var sx = (double)preview.Width / observations.ImageSizeWidthHeight[0];
        var sy = (double)preview.Height / observations.ImageSizeWidthHeight[1];
        var csv = new StringBuilder("corner,observed_x,observed_y,expected_x,expected_y,dx,dy,error_px\n");
        for (var j = 0; j < source.PointsXy.Length; j++)
        {
            var p = observations.ObjectPointsXyz[j];
            var e = solution.Extrinsics[index];
            var x = e[0][0] * p[0] + e[0][1] * p[1] + e[0][2] * p[2] + e[0][3];
            var y = e[1][0] * p[0] + e[1][1] * p[1] + e[1][2] * p[2] + e[1][3];
            var z = e[2][0] * p[0] + e[2][1] * p[1] + e[2][2] * p[2] + e[2][3];
            var planar = Math.Sqrt(x * x + y * y);
            var theta = Math.Atan2(planar, z);
            var radius = 0.0;
            for (var k = solution.IncidentAngleToRadiusPolynomial.Length - 1; k >= 0; k--)
                radius = radius * theta + solution.IncidentAngleToRadiusPolynomial[k];
            var px = solution.PrincipalPointXy[0] + (planar > 1e-15 ? x * radius / planar : 0);
            var py = solution.PrincipalPointXy[1] + (planar > 1e-15 ? y * radius / planar : 0);
            var ox = source.PointsXy[j][0]; var oy = source.PointsXy[j][1];
            var dx = px - ox; var dy = py - oy;
            csv.AppendLine(FormattableString.Invariant($"{j},{ox:R},{oy:R},{px:R},{py:R},{dx:R},{dy:R},{Math.Sqrt(dx * dx + dy * dy):R}"));
            Cv2.Circle(overlay, new Point(ox * sx, oy * sy), 4, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            Cv2.DrawMarker(overlay, new Point(px * sx, py * sy), Scalar.Red, MarkerTypes.Cross, 9, 1, LineTypes.AntiAlias);
        }
        Cv2.PutText(overlay, "Observed: green circles / Fitted: red crosses", new Point(12, 24),
            HersheyFonts.HersheySimplex, 0.45, Scalar.White, 1, LineTypes.AntiAlias);
        var stem = $"{index:D3}-{Path.GetFileNameWithoutExtension(source.Name)}";
        File.WriteAllText(Path.Combine(directory, stem + ".csv"), csv.ToString());
        if (!Cv2.ImWrite(Path.Combine(directory, stem + ".jpg"), overlay,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 85)))
            throw new IOException("Unable to write debug image for " + source.Name);
    }
}

public sealed record OmniCalibRun(CalibrationObservationDocument Observations, OmniCalibrationSolution Solution,
    string YamlPath, string DebugDirectory, double DetectionMilliseconds, double SolveMilliseconds,
    double OutputMilliseconds, double TotalMilliseconds);
public sealed record CalibrationImageTiming(string Path, bool Found, double ReadDecodeMilliseconds,
    double FullSharpenMilliseconds, double ResizeMilliseconds, double SmallSharpenMilliseconds,
    double ClassicMilliseconds, double SbMilliseconds, double SubpixelMilliseconds,
    double PreviewMilliseconds, double HashMilliseconds,
    int ClassicAttempts = 0, int SectorAttempts = 0, int ExhaustiveAttempts = 0);
public sealed record PortDetectionProfile(CalibrationObservationDocument Observations,
    double WallMilliseconds, IReadOnlyList<CalibrationImageTiming> Images);
internal sealed class PortCheckerboardExtractor
{
    // setNumThreads is process-global and explicitly not thread-safe. Holding
    // this lock prevents two simultaneous calibrations from changing it while
    // either one has OpenCV work in flight.
    public CalibrationObservationDocument Extract(string imageDirectory, CheckerboardDetectionSettings settings,
        Action<string, Mat>? capturePreview = null, Action<CalibrationImageTiming>? profile = null)
    {
        lock (OmniCalibCSharpPort.OpenCvExecutionLock)
        {
            var previousThreads = Cv2.GetNumThreads();
            try { return ExtractCore(imageDirectory, settings, capturePreview, profile); }
            finally { Cv2.SetNumThreads(previousThreads); }
        }
    }

    private static CalibrationObservationDocument ExtractCore(
        string imageDirectory,
        CheckerboardDetectionSettings settings, Action<string, Mat>? capturePreview = null,
        Action<CalibrationImageTiming>? profile = null)
    {
        if (!Directory.Exists(imageDirectory))
            throw new DirectoryNotFoundException($"Calibration image directory does not exist: {imageDirectory}");
        if (settings.InnerColumns < 2 || settings.InnerRows < 2)
            throw new ArgumentOutOfRangeException(nameof(settings), "A checkerboard needs at least 2 x 2 inner corners.");
        if (settings.DownsampleFactor < 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Downsample factor must be positive.");
        if (!Enum.IsDefined(settings.SearchMode)) throw new ArgumentOutOfRangeException(nameof(settings));
        if (!double.IsFinite(settings.SquareSizeMillimetres) || settings.SquareSizeMillimetres <= 0 ||
            !double.IsFinite(settings.SubpixelEpsilon) || settings.SubpixelEpsilon <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Square size and subpixel epsilon must be finite and positive.");
        if (settings.OpenCvThreadCount < 1 || settings.ImageWorkerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Thread and worker counts must be positive.");
        settings = settings with { ImageWorkerCount = Math.Min(settings.ImageWorkerCount,
            PortHardwarePolicy.RecommendWorkers(Environment.ProcessorCount, PortHardwarePolicy.GetMemoryBudget())) };

        var candidates = Directory.EnumerateFiles(imageDirectory)
            .Where(path => new[] { ".jpg", ".jpeg", ".png" }.Contains(
                Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(NaturalImageKey)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("No JPEG or PNG calibration images were found.");

        Cv2.SetNumThreads(settings.OpenCvThreadCount);
        var pattern = new Size(settings.InnerColumns, settings.InnerRows);
        var detections = new DetectionResult?[candidates.Length];
        var workerCount = Math.Min(settings.ImageWorkerCount, candidates.Length);
        // Dedicated bounded workers avoid thread-pool ramp-up on the first large image batch.
        // Keep the native worker pool at one thread by default to avoid nested oversubscription.
        var nextImage = -1;
        var workers = new Task[workerCount];
        for (var worker = 0; worker < workerCount; worker++)
            workers[worker] = Task.Factory.StartNew(() =>
            {
                int index;
                while ((index = Interlocked.Increment(ref nextImage)) < candidates.Length)
                    detections[index] = DetectOne(candidates[index], pattern, settings, capturePreview, profile);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task.WaitAll(workers);

        var accepted = detections.Where(result => result is not null).Select(result => result!).ToArray();
        var observations = accepted.Select(result => result.Observation).ToArray();

        if (observations.Length == 0)
            throw new InvalidDataException("No complete checkerboards were detected.");
        var imageWidth = accepted[0].Width;
        var imageHeight = accepted[0].Height;
        if (accepted.Any(result => result.Width != imageWidth || result.Height != imageHeight))
            throw new InvalidDataException("All calibration images must have the same oriented dimensions.");

        var worldPoints = new List<double[]>();
        for (var row = 0; row < settings.InnerRows; row++)
        for (var column = 0; column < settings.InnerColumns; column++)
            worldPoints.Add(new[]
            {
                column * settings.SquareSizeMillimetres,
                row * settings.SquareSizeMillimetres,
                0.0
            });

        return new CalibrationObservationDocument(
            1,
            "xy_zero_based_after_exif_orientation",
            [imageWidth, imageHeight],
            [settings.InnerColumns, settings.InnerRows],
            settings.SquareSizeMillimetres,
            new CalibrationDetectorMetadata(
                Cv2.GetVersionString() ?? "unknown", settings.DownsampleFactor, settings.SubpixelEpsilon,
                $"{(settings.ReadImageOnce ? "single-read imdecode" : "imread")}, " +
                $"{(settings.SharpenFullResolution ? "full sharpen, " : "")}downsample, sharpen, " +
                $"{settings.SearchMode}, full-resolution subpixel",
                settings.OpenCvThreadCount, settings.ImageWorkerCount),
            observations,
            worldPoints.ToArray());
    }

    private static DetectionResult? DetectOne(
        string path,
        Size pattern,
        CheckerboardDetectionSettings settings, Action<string, Mat>? capturePreview = null,
        Action<CalibrationImageTiming>? profile = null)
    {
        var tick = Stopwatch.GetTimestamp();
        var encoded = settings.ReadImageOnce ? File.ReadAllBytes(path) : null;
        using var gray = encoded is null ? Cv2.ImRead(path, ImreadModes.Grayscale)
            : encoded.Length == 0 ? new Mat() : Cv2.ImDecode(encoded, ImreadModes.Grayscale);
        var readMs = Mark(ref tick);
        if (gray.Empty())
        {
            profile?.Invoke(new(path, false, readMs, 0, 0, 0, 0, 0, 0, 0, 0));
            return null;
        }
        if (gray.Width / settings.DownsampleFactor < 1 || gray.Height / settings.DownsampleFactor < 1)
            throw new InvalidDataException("Downsample factor exceeds image dimensions: " + path);
        using var sharpened = settings.SharpenFullResolution ? Sharpen(gray) : null;
        var fullSharpenMs = Mark(ref tick);
        using var small = new Mat();
        Cv2.Resize(sharpened ?? gray, small,
            new Size(gray.Width / settings.DownsampleFactor, gray.Height / settings.DownsampleFactor),
            interpolation: InterpolationFlags.Area);
        var resizeMs = Mark(ref tick);
        using var smallSharpened = Sharpen(small);
        var smallSharpenMs = Mark(ref tick);

        Point2f[] corners = [];
        var classicMs = 0.0;
        var sbMs = 0.0;
        var classicAttempts = 0;
        var sectorAttempts = 0;
        var exhaustiveAttempts = 0;
        bool Classic()
        {
            classicAttempts++;
            var start = Stopwatch.GetTimestamp();
            var foundBoard = Cv2.FindChessboardCorners(smallSharpened, pattern, out corners,
                ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage);
            classicMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return foundBoard;
        }
        bool Sector(ChessboardFlags flags)
        {
            sectorAttempts++;
            if ((flags & ChessboardFlags.Exhaustive) != 0) exhaustiveAttempts++;
            var start = Stopwatch.GetTimestamp();
            var foundBoard = Cv2.FindChessboardCornersSB(smallSharpened, pattern, out corners, flags);
            sbMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return foundBoard;
        }
        var found = settings.SearchMode switch
        {
            CheckerboardSearchMode.SectorFirst => Sector(ChessboardFlags.Exhaustive) || Classic(),
            CheckerboardSearchMode.SectorFastFirst => Sector(0) || Sector(ChessboardFlags.Exhaustive) || Classic(),
            _ => Classic() || Sector(ChessboardFlags.Exhaustive)
        };
        tick = Stopwatch.GetTimestamp();
        if (!found)
        {
            profile?.Invoke(new(path, false, readMs, fullSharpenMs, resizeMs, smallSharpenMs,
                classicMs, sbMs, 0, 0, 0, classicAttempts, sectorAttempts, exhaustiveAttempts));
            return null;
        }

        var scaleX = (float)gray.Width / small.Width;
        var scaleY = (float)gray.Height / small.Height;
        for (var index = 0; index < corners.Length; index++)
            corners[index] = new Point2f(corners[index].X * scaleX, corners[index].Y * scaleY);
        corners = Cv2.CornerSubPix(
            gray, corners, new Size(11, 11), new Size(-1, -1),
            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 50, settings.SubpixelEpsilon));
        var subpixelMs = Mark(ref tick);

        capturePreview?.Invoke(Path.GetFullPath(path), gray);
        var previewMs = Mark(ref tick);
        var hash = Convert.ToHexString(SHA256.HashData(encoded ?? File.ReadAllBytes(path))).ToLowerInvariant();
        var hashMs = Mark(ref tick);
        profile?.Invoke(new(path, true, readMs, fullSharpenMs, resizeMs, smallSharpenMs,
            classicMs, sbMs, subpixelMs, previewMs, hashMs, classicAttempts, sectorAttempts, exhaustiveAttempts));
        return new DetectionResult(
            gray.Width,
            gray.Height,
            new CalibrationImageObservation(
                Path.GetFullPath(path),
                Path.GetFileName(path),
                hash,
                corners.Select(point => new[] { (double)point.X, (double)point.Y }).ToArray()));
    }

    private static double Mark(ref long timestamp)
    {
        var next = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(timestamp, next).TotalMilliseconds;
        timestamp = next;
        return elapsed;
    }

    private static Mat Sharpen(Mat source)
    {
        using var kernel = Mat.FromPixelData(3, 3, MatType.CV_32FC1,
            new float[] { 0, -1, 0, -1, 5, -1, 0, -1, 0 });
        var destination = new Mat();
        Cv2.Filter2D(source, destination, MatType.CV_8UC1, kernel);
        return destination;
    }

    private static string NaturalImageKey(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? $"0-{number:D12}"
            : $"1-{Path.GetFileName(path).ToLowerInvariant()}";
    }

    private sealed record DetectionResult(int Width, int Height, CalibrationImageObservation Observation);
}


/// <summary>
/// Native port of Thomas Poenitz's MIT-licensed py-omnicalib calibration pipeline.
/// It retains the Scaramuzza central polynomial model and never substitutes
/// OpenCV's Kannala-Brandt fisheye model.
/// </summary>
public sealed class OmniCalibrator
{
    private static readonly MatrixBuilder<double> M = Matrix<double>.Build;
    private static readonly VectorBuilder<double> V = Vector<double>.Build;

public OmniCalibrationSolution Solve(
        CalibrationObservationDocument observations,
        OmniCalibrationOptions? options = null,
        Action<string>? progress = null)
    {
        options ??= new OmniCalibrationOptions();
        ValidateInputs(observations, options);

        var imagePoints = observations.Images.Select(image =>
            image.PointsXy.Select(V.DenseOfArray).ToArray()).ToArray();
        var worldTemplate = observations.ObjectPointsXyz.Select(V.DenseOfArray).ToArray();
        var worldPoints = Enumerable.Range(0, imagePoints.Length)
            .Select(_ => worldTemplate.Select(point => point.Clone()).ToArray()).ToArray();
        var initialPrincipal = V.DenseOfArray(new[]
        {
            observations.ImageSizeWidthHeight[0] * 0.5 - 0.5,
            observations.ImageSizeWidthHeight[1] * 0.5 - 0.5
        });

        var required = options.MinimumInitializationImages
            ?? Math.Max(1, (int)Math.Round(imagePoints.Length / 4.0));
        required = Math.Clamp(required, 1, imagePoints.Length);

        Vector<double>? principal = null;
        List<InitialCandidate?>? candidates = null;
        foreach (var offset in Spiral(options.PrincipalPointSearchStepPixels,
                     options.PrincipalPointSearchRadiusPixels))
        {
            principal = initialPrincipal + V.DenseOfArray(new[] { (double)offset.X, offset.Y });
            var candidateArray = new InitialCandidate?[imagePoints.Length];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaximumDegreeOfParallelism <= 0
                    ? Environment.ProcessorCount
                    : options.MaximumDegreeOfParallelism
            };
            Parallel.For(0, imagePoints.Length, parallelOptions, imageIndex =>
            {
                var centered = Center(imagePoints[imageIndex], principal);
                candidateArray[imageIndex] = FindInitialCandidate(
                    centered,
                    worldPoints[imageIndex],
                    options.PolynomialDegree,
                    options.InitializationMeanErrorThresholdPixels);
            });
            candidates = candidateArray.ToList();

            var validCount = candidates.Count(candidate => candidate is not null);
            progress?.Invoke($"Principal point ({principal[0]:F1}, {principal[1]:F1}): " +
                             $"{validCount}/{imagePoints.Length} usable initial poses");
            if (validCount >= required) break;
        }

        if (principal is null || candidates is null || candidates.Count(candidate => candidate is not null) < required)
            throw new InvalidOperationException("No valid OmniCalib initialization was found in the configured principal-point search area.");

        var validCandidates = candidates
            .Select((candidate, index) => (Candidate: candidate, Index: index))
            .Where(item => item.Candidate is not null)
            .Select(item => (Candidate: item.Candidate!, item.Index))
            .ToArray();
        var selectionThreshold = validCandidates.Select(item => item.Candidate.MeanError)
            .OrderBy(value => value).ElementAt(required - 1);
        var selected = validCandidates.Where(item => item.Candidate.MeanError <= selectionThreshold).ToArray();
        var initializationMask = new bool[imagePoints.Length];
        foreach (var item in selected) initializationMask[item.Index] = true;

        progress?.Invoke($"Refining the best {selected.Length}/{imagePoints.Length} initialized poses");
        var subsetImage = selected.Select(item => imagePoints[item.Index]).ToArray();
        var subsetWorld = selected.Select(item => worldPoints[item.Index]).ToArray();
        var subsetRotations = selected.Select(item => item.Candidate.Rotation.Clone()).ToArray();
        var subsetPartialTranslations = selected.Select(item => item.Candidate.PartialTranslation.Clone()).ToArray();
        var subsetFit = FitRadiusToZPolynomial(
            options.PolynomialDegree,
            subsetImage.Select(points => Center(points, principal)).ToArray(),
            subsetWorld,
            subsetRotations,
            subsetPartialTranslations);
        var subsetTheta = FitIncidentAnglePolynomial(
            subsetFit.Polynomial,
            subsetImage,
            principal,
            options.PolynomialDegree);
        var subsetState = new CalibrationState(
            subsetRotations,
            subsetFit.Translations,
            subsetTheta,
            principal.Clone());
        var subsetOptimized = Optimize(subsetState, subsetImage, subsetWorld, options, progress, "subset");
        if (!subsetOptimized.Diagnostics.Converged)
            throw new InvalidOperationException($"Subset calibration refinement failed: {subsetOptimized.Diagnostics.TerminationReason}");

        var subsetRadiusToZ = FitRadiusToZFromIncidentPolynomial(
            subsetOptimized.State, subsetWorld, options.PolynomialDegree);
        var allExtrinsics = SolveFullExtrinsics(
            subsetRadiusToZ,
            imagePoints.Select(points => Center(points, subsetOptimized.State.PrincipalPoint)).ToArray(),
            worldPoints);
        var allState = new CalibrationState(
            allExtrinsics.Rotations,
            allExtrinsics.Translations,
            subsetOptimized.State.IncidentPolynomial.ToArray(),
            subsetOptimized.State.PrincipalPoint.Clone());

        progress?.Invoke($"Jointly refining all {imagePoints.Length} poses and lens parameters");
        var finalOptimized = Optimize(allState, imagePoints, worldPoints, options, progress, "final");
        if (!finalOptimized.Diagnostics.Converged)
            throw new InvalidOperationException($"Final calibration refinement failed: {finalOptimized.Diagnostics.TerminationReason}");
        if (finalOptimized.State.Rotations.Zip(finalOptimized.State.Translations).Any(pair =>
                !CheckOrigin(pair.First, pair.Second)))
            throw new InvalidOperationException("The optimized solution places one or more boards behind the camera-facing plane.");

        var finalRadiusToZ = FitRadiusToZFromIncidentPolynomial(
            finalOptimized.State, worldPoints, options.PolynomialDegree);
        return new OmniCalibrationSolution(
            finalOptimized.State.Rotations.Select(ToRows).ToArray(),
            finalOptimized.State.Translations.Select(vector => vector.ToArray()).ToArray(),
            finalOptimized.State.IncidentPolynomial.ToArray(),
            finalRadiusToZ,
            finalOptimized.State.PrincipalPoint.ToArray(),
            initializationMask,
            subsetOptimized.Diagnostics,
            finalOptimized.Diagnostics);
    }

    private static InitialCandidate? FindInitialCandidate(
        Vector<double>[] centeredImage,
        Vector<double>[] world,
        int degree,
        double errorThreshold)
    {
        try
        {
            var partial = SolvePartialExtrinsics(centeredImage, world);
            InitialCandidate? best = null;
            foreach (var candidate in OrthonormalCandidates(partial.Rotation, partial.Translation))
            {
                try
                {
                    var fit = FitRadiusToZPolynomial(
                        degree,
                        new[] { centeredImage },
                        new[] { world },
                        new[] { candidate.Rotation },
                        new[] { candidate.Translation });
                    if (fit.Polynomial[0] < 0 || !CheckOrigin(candidate.Rotation, fit.Translations[0])) continue;
                    var mean = MeanRadiusToZReprojectionError(
                        centeredImage, world, candidate.Rotation, fit.Translations[0], fit.Polynomial);
                    if (double.IsFinite(mean) && mean < errorThreshold && (best is null || mean < best.MeanError))
                        best = new InitialCandidate(mean, candidate.Rotation, candidate.Translation);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    // Degenerate algebraic candidates are expected during the sign/permutation search.
                }
            }

            return best;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static PartialPose SolvePartialExtrinsics(Vector<double>[] image, Vector<double>[] world)
    {
        var worldValues = world.SelectMany(point => point.Take(2)).ToArray();
        var imageValues = image.SelectMany(point => point).ToArray();
        var scale = SampleStandardDeviation(worldValues) / SampleStandardDeviation(imageValues);
        var a = M.Dense(image.Length, 6);
        for (var index = 0; index < image.Length; index++)
        {
            var u = image[index][0] * scale;
            var v = image[index][1] * scale;
            var x = world[index][0];
            var y = world[index][1];
            a[index, 0] = -v * x;
            a[index, 1] = -v * y;
            a[index, 2] = u * x;
            a[index, 3] = u * y;
            a[index, 4] = -v;
            a[index, 5] = u;
        }

        var nullVector = NullVector(a);
        return new PartialPose(
            M.DenseOfArray(new[,] { { nullVector[0], nullVector[1] }, { nullVector[2], nullVector[3] } }),
            V.DenseOfArray(new[] { nullVector[4], nullVector[5] }));
    }

    private static IEnumerable<PartialPose> OrthonormalCandidates(Matrix<double> rotation, Vector<double> translation)
    {
        foreach (var sign in new[] { 1.0, -1.0 })
        {
            var r = rotation * sign;
            var t = translation * sign;

            foreach (var b3 in SolveR21(r))
                if (TrySolveScale(b3, r, out var scaled, out var scale))
                    yield return new PartialPose(SolveFullRotation(scaled), t * scale);

            foreach (var b3 in SolveR21(r.Transpose()))
                if (TrySolveScale(b3, r, out var scaled, out var scale))
                    yield return new PartialPose(SolveFullRotation(scaled).Transpose(), t * scale);

            var flipped = FlipColumns(r);
            foreach (var b3 in SolveR21(flipped))
                if (TrySolveScale(b3, flipped, out var scaled, out var scale))
                    yield return new PartialPose(SolveFullRotation(FlipColumns(scaled)), t * scale);

            var transposedFlipped = FlipColumns(r.Transpose());
            foreach (var b3 in SolveR21(transposedFlipped))
                if (TrySolveScale(b3, transposedFlipped, out var scaled, out var scale))
                    yield return new PartialPose(SolveFullRotation(FlipColumns(scaled)).Transpose(), t * scale);
        }
    }

    private static IEnumerable<double> SolveR21(Matrix<double> r)
    {
        var a1 = r[0, 0];
        var a2 = r[1, 0];
        var b1 = r[0, 1];
        var b2 = r[1, 1];
        var p = b1 * b1 + b2 * b2 - a1 * a1 + a2 * a2;
        var q = -Math.Pow(a1 * b1 + a2 * b2, 2);
        var discriminant = p * p / 4 - q;
        if (discriminant < 0) yield break;
        var squared = -p / 2 + Math.Sqrt(discriminant);
        if (squared >= 0)
        {
            var value = Math.Sqrt(squared);
            if (double.IsFinite(value)) yield return value;
        }
    }

    private static bool TrySolveScale(
        double b3,
        Matrix<double> r,
        out Matrix<double> scaled,
        out double scale)
    {
        scaled = M.Dense(3, 2);
        scaled.SetSubMatrix(0, 2, 0, 2, r);
        scaled[2, 1] = b3;
        var norm = scaled.Column(1).L2Norm();
        scale = 1.0 / norm;
        scaled *= scale;
        var squared = scaled[0, 0] * scaled[0, 0] + scaled[1, 0] * scaled[1, 0];
        if (!double.IsFinite(scale) || squared > 1.0 + 1e-12) return false;
        scaled[2, 0] = Math.Sqrt(Math.Max(0, 1.0 - squared));
        return true;
    }

    private static Matrix<double> SolveFullRotation(Matrix<double> firstTwoColumns)
    {
        var orthonormal = GramSchmidt(firstTwoColumns);
        var third = Cross(orthonormal.Column(0), orthonormal.Column(1));
        third /= third.L2Norm();
        var result = M.Dense(3, 3);
        result.SetColumn(0, orthonormal.Column(0));
        result.SetColumn(1, orthonormal.Column(1));
        result.SetColumn(2, third);
        return result;
    }

    private static PolynomialFit FitRadiusToZPolynomial(
        int degree,
        Vector<double>[][] image,
        Vector<double>[][] world,
        Matrix<double>[] rotations,
        Vector<double>[] partialTranslations)
    {
        var imageCount = image.Length;
        var pointCount = image[0].Length;
        var coefficientCount = degree;
        var a = M.Dense(imageCount * pointCount * 2, coefficientCount + imageCount);
        var q = V.Dense(imageCount * pointCount * 2);

        for (var imageIndex = 0; imageIndex < imageCount; imageIndex++)
        {
            var r = rotations[imageIndex];
            var t = partialTranslations[imageIndex];
            var rowBase = imageIndex * pointCount * 2;
            for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                var u = image[imageIndex][pointIndex][0];
                var v = image[imageIndex][pointIndex][1];
                var x = world[imageIndex][pointIndex][0];
                var y = world[imageIndex][pointIndex][1];
                var first = r[1, 0] * x + r[1, 1] * y + t[1];
                var firstRight = v * (r[2, 0] * x + r[2, 1] * y);
                var second = r[0, 0] * x + r[0, 1] * y + t[0];
                var secondRight = u * (r[2, 0] * x + r[2, 1] * y);
                var radius = Math.Sqrt(u * u + v * v);

                for (var coefficient = 0; coefficient < coefficientCount; coefficient++)
                {
                    var power = coefficient == 0 ? 0 : coefficient + 1;
                    var basis = Math.Pow(radius, power);
                    a[rowBase + pointIndex, coefficient] = basis * first;
                    a[rowBase + pointCount + pointIndex, coefficient] = basis * second;
                }

                a[rowBase + pointIndex, coefficientCount + imageIndex] = -v;
                a[rowBase + pointCount + pointIndex, coefficientCount + imageIndex] = -u;
                q[rowBase + pointIndex] = firstRight;
                q[rowBase + pointCount + pointIndex] = secondRight;
            }
        }

        var solution = SolveLeastSquares(a, q);
        var polynomial = new double[degree + 1];
        polynomial[0] = solution[0];
        for (var power = 2; power <= degree; power++) polynomial[power] = solution[power - 1];
        var translations = partialTranslations.Select((partial, index) =>
            V.DenseOfArray(new[] { partial[0], partial[1], solution[coefficientCount + index] })).ToArray();
        return new PolynomialFit(polynomial, translations);
    }

    private static double[] FitIncidentAnglePolynomial(
        double[] radiusToZ,
        Vector<double>[][] image,
        Vector<double> principal,
        int degree)
    {
        var rows = image.Sum(points => points.Length);
        var a = M.Dense(rows, degree);
        var target = V.Dense(rows);
        var row = 0;
        foreach (var points in image)
            foreach (var point in points)
            {
                var dx = point[0] - principal[0];
                var dy = point[1] - principal[1];
                var radius = Math.Sqrt(dx * dx + dy * dy);
                var z = EvaluatePolynomial(radiusToZ, radius);
                var theta = Math.Atan(radius / z);
                for (var power = 1; power <= degree; power++) a[row, power - 1] = Math.Pow(theta, power);
                target[row++] = radius;
            }

        var fitted = SolveLeastSquares(a, target);
        return new[] { 0.0 }.Concat(fitted).ToArray();
    }

    private static double[] FitRadiusToZFromIncidentPolynomial(
        CalibrationState state,
        Vector<double>[][] world,
        int degree)
    {
        var rows = world.Sum(points => points.Length);
        var a = M.Dense(rows, degree);
        var target = V.Dense(rows);
        var row = 0;
        for (var imageIndex = 0; imageIndex < world.Length; imageIndex++)
            foreach (var point in world[imageIndex])
            {
                var (x, y, viewZ) = TransformPoint(state.Rotations[imageIndex], point,
                    state.Translations[imageIndex]);
                var theta = Math.Atan2(Math.Sqrt(x * x + y * y), viewZ);
                var radius = EvaluatePolynomial(state.IncidentPolynomial, theta);
                var z = radius / Math.Tan(theta);
                for (var coefficient = 0; coefficient < degree; coefficient++)
                {
                    var power = coefficient == 0 ? 0 : coefficient + 1;
                    a[row, coefficient] = Math.Pow(radius, power);
                }
                target[row++] = z;
            }

        var fitted = SolveLeastSquares(a, target);
        var polynomial = new double[degree + 1];
        polynomial[0] = fitted[0];
        for (var power = 2; power <= degree; power++) polynomial[power] = fitted[power - 1];
        return polynomial;
    }

    private static FullPoses SolveFullExtrinsics(
        double[] radiusToZ,
        Vector<double>[][] image,
        Vector<double>[][] world)
    {
        var rotations = new Matrix<double>[image.Length];
        var translations = new Vector<double>[image.Length];
        for (var imageIndex = 0; imageIndex < image.Length; imageIndex++)
        {
            var pointCount = image[imageIndex].Length;
            var a = M.Dense(pointCount * 3, 9);
            for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                var u = image[imageIndex][pointIndex][0];
                var v = image[imageIndex][pointIndex][1];
                var x = world[imageIndex][pointIndex][0];
                var y = world[imageIndex][pointIndex][1];
                var rho = EvaluatePolynomial(radiusToZ, Math.Sqrt(u * u + v * v));
                var row = pointIndex * 3;
                a[row, 0] = rho * x; a[row, 1] = rho * y; a[row, 2] = rho;
                a[row, 6] = -u * x; a[row, 7] = -u * y; a[row, 8] = -u;
                a[row + 1, 0] = -v * x; a[row + 1, 1] = -v * y; a[row + 1, 2] = -v;
                a[row + 1, 3] = u * x; a[row + 1, 4] = u * y; a[row + 1, 5] = u;
                a[row + 2, 3] = -rho * x; a[row + 2, 4] = -rho * y; a[row + 2, 5] = -rho;
                a[row + 2, 6] = v * x; a[row + 2, 7] = v * y; a[row + 2, 8] = v;
            }

            var nullVector = NullVector(a);
            var pose = BuildFullPose(nullVector);
            if (!CheckOrigin(pose.Rotation, pose.Translation)) pose = BuildFullPose(-nullVector);
            rotations[imageIndex] = pose.Rotation;
            translations[imageIndex] = pose.Translation;
        }
        return new FullPoses(rotations, translations);
    }

    private static FullPose BuildFullPose(Vector<double> nullVector)
    {
        var matrix = M.Dense(3, 3, (row, column) => nullVector[row * 3 + column]);
        var firstTwo = matrix.SubMatrix(0, 3, 0, 2);
        var normalization = (firstTwo.Column(0).L2Norm() + firstTwo.Column(1).L2Norm()) * 0.5;
        var rotation = SolveFullRotation(firstTwo);
        return new FullPose(rotation, matrix.Column(2) / normalization);
    }

    private static OptimizationResult Optimize(
        CalibrationState initial,
        Vector<double>[][] image,
        Vector<double>[][] world,
        OmniCalibrationOptions options,
        Action<string>? progress,
        string stage)
    {
        var state = initial.DeepCopy();
        var evaluation = Evaluate(state, image, world, includeJacobian: true);
        var initialCost = evaluation.Cost;
        var lambda = 1e-3;
        var accepted = 0;
        var evaluations = 1;
        var reason = $"maximum iterations ({options.MaximumIterations}) reached";
        var converged = false;
        var iterations = 0;

        for (iterations = 1; iterations <= options.MaximumIterations; iterations++)
        {
            var jacobian = evaluation.Jacobian!;
            var (normal, gradient) = BuildNormalEquations(jacobian, evaluation.Residuals, options.Kernel);
            if (gradient.AbsoluteMaximum() <= options.GradientTolerance)
            {
                converged = true;
                reason = "gradient tolerance reached";
                break;
            }

            var damped = normal.Clone();
            for (var index = 0; index < damped.RowCount; index++)
                damped[index, index] += lambda * Math.Max(normal[index, index], 1e-12);

            Vector<double> step;
            try
            {
                // Levenberg damping makes the normal matrix positive definite.
                // Retain SVD for numerically degenerate cases.
                try { step = damped.Cholesky().Solve(-gradient); }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    step = damped.Svd(true).Solve(-gradient);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                lambda *= 10;
                if (lambda > 1e18) { reason = "normal equations became singular"; break; }
                continue;
            }

            if (!step.All(double.IsFinite))
            {
                reason = "optimizer produced a non-finite step";
                break;
            }

            if (step.L2Norm() <= options.StepTolerance * (1 + StateScale(state)))
            {
                converged = true;
                reason = "step tolerance reached";
                break;
            }

            var candidate = ApplyStep(state, step);
            var candidateEvaluation = Evaluate(candidate, image, world, includeJacobian: false);
            evaluations++;
            if (double.IsFinite(candidateEvaluation.Cost) && candidateEvaluation.Cost < evaluation.Cost)
            {
                var previousCost = evaluation.Cost;
                state = candidate;
                evaluation = Evaluate(state, image, world, includeJacobian: true);
                evaluations++;
                accepted++;
                lambda = Math.Max(1e-15, lambda / 3);
                if (iterations == 1 || iterations % 10 == 0)
                    progress?.Invoke($"{stage} refinement iteration {iterations}: " +
                                     $"RMSE/coordinate={Math.Sqrt(2 * evaluation.Cost / evaluation.Residuals.Count):F6} px");
                if (previousCost - evaluation.Cost <= options.FunctionTolerance * Math.Max(1.0, previousCost))
                {
                    converged = true;
                    reason = "function tolerance reached";
                    break;
                }
            }
            else
            {
                lambda *= 10;
                if (lambda > 1e18)
                {
                    reason = "damping limit reached without a decreasing step";
                    break;
                }
            }
        }

        var diagnostics = new OmniCalibrationDiagnostics(
            converged, reason, Math.Min(iterations, options.MaximumIterations), accepted, evaluations,
            initialCost, evaluation.Cost);
        return new OptimizationResult(state, diagnostics);
    }

    /// <summary>The only hardware dispatch in the managed solver. No unsupported ISA is invoked.</summary>
    public static string GetKernelDescription(OmniSolverKernel kernel = OmniSolverKernel.Auto) => kernel switch
    {
        OmniSolverKernel.MathNet => "MathNet matrix products",
        OmniSolverKernel.Auto when UseWideSimd =>
            $"SIMD dot products ({SimdDouble.Count} doubles per vector)",
        OmniSolverKernel.Auto => "MathNet fallback (wide SIMD unavailable)",
        _ => "Scalar comparison kernel"
    };

    // The measured benefit is for vectors of at least four doubles. Smaller/no SIMD
    // keeps the original implementation rather than the slower scalar comparison kernel.
    private static bool UseWideSimd => System.Numerics.Vector.IsHardwareAccelerated && SimdDouble.Count >= 4;

    internal static (Matrix<double> Normal, Vector<double> Gradient) BuildNormalEquations(
        Matrix<double> jacobian, Vector<double> residual, OmniSolverKernel kernel)
    {
        if (kernel == OmniSolverKernel.MathNet || (kernel == OmniSolverKernel.Auto && !UseWideSimd) ||
            jacobian.Storage is not MathNet.Numerics.LinearAlgebra.Storage.DenseColumnMajorMatrixStorage<double> storage ||
            residual.Storage is not MathNet.Numerics.LinearAlgebra.Storage.DenseVectorStorage<double> residualStorage)
            return (jacobian.TransposeThisAndMultiply(jacobian), jacobian.TransposeThisAndMultiply(residual));

        var rows = jacobian.RowCount;
        var columns = jacobian.ColumnCount;
        var normal = M.Dense(columns, columns);
        var gradient = V.Dense(columns);
        var simd = kernel == OmniSolverKernel.Auto && System.Numerics.Vector.IsHardwareAccelerated;
        // MathNet dense storage is column-major: each dot product is a contiguous read.
        // Compute only one symmetric triangle and reuse it for the other half.
        for (var column = 0; column < columns; column++)
        {
            var offset = column * rows;
            gradient[column] = Dot(storage.Data, offset, residualStorage.Data, 0, rows, simd);
            for (var other = 0; other <= column; other++)
            {
                var value = Dot(storage.Data, offset, storage.Data, other * rows, rows, simd);
                normal[column, other] = value;
                normal[other, column] = value;
            }
        }
        return (normal, gradient);
    }

    internal static double Dot(double[] left, int leftOffset, double[] right, int rightOffset, int count, bool simd)
    {
        // Validate complete slices once. Every vector load below stays within these
        // slices; no overread is permitted for a short final vector.
        var leftSpan = left.AsSpan(leftOffset, count);
        var rightSpan = right.AsSpan(rightOffset, count);
        ref var leftStart = ref MemoryMarshal.GetReference(leftSpan);
        ref var rightStart = ref MemoryMarshal.GetReference(rightSpan);
        var i = 0;
        var sum = 0.0;
        if (simd && System.Numerics.Vector.IsHardwareAccelerated)
        {
            // Independent accumulators hide the vector-add dependency latency.
            var accumulator = SimdDouble.Zero;
            var second = SimdDouble.Zero;
            var third = SimdDouble.Zero;
            var fourth = SimdDouble.Zero;
            var width = SimdDouble.Count;
            for (; i <= count - 4 * width; i += 4 * width)
            {
                accumulator += System.Numerics.Vector.LoadUnsafe(ref leftStart, (nuint)i) * System.Numerics.Vector.LoadUnsafe(ref rightStart, (nuint)i);
                second += System.Numerics.Vector.LoadUnsafe(ref leftStart, (nuint)(i + width)) * System.Numerics.Vector.LoadUnsafe(ref rightStart, (nuint)(i + width));
                third += System.Numerics.Vector.LoadUnsafe(ref leftStart, (nuint)(i + 2 * width)) * System.Numerics.Vector.LoadUnsafe(ref rightStart, (nuint)(i + 2 * width));
                fourth += System.Numerics.Vector.LoadUnsafe(ref leftStart, (nuint)(i + 3 * width)) * System.Numerics.Vector.LoadUnsafe(ref rightStart, (nuint)(i + 3 * width));
            }
            accumulator = (accumulator + second) + (third + fourth);
            for (; i <= count - SimdDouble.Count; i += SimdDouble.Count)
                accumulator += System.Numerics.Vector.LoadUnsafe(ref leftStart, (nuint)i) * System.Numerics.Vector.LoadUnsafe(ref rightStart, (nuint)i);
            for (var lane = 0; lane < SimdDouble.Count; lane++) sum += accumulator[lane];
        }
        for (; i < count; i++) sum += leftSpan[i] * rightSpan[i];
        return sum;
    }

    private static Evaluation Evaluate(
        CalibrationState state,
        Vector<double>[][] image,
        Vector<double>[][] world,
        bool includeJacobian)
    {
        var imageCount = image.Length;
        var pointCount = image[0].Length;
        var degree = state.IncidentPolynomial.Length - 1;
        var parameterCount = imageCount * 6 + degree + 2;
        var residual = V.Dense(imageCount * pointCount * 2);
        var jacobian = includeJacobian ? M.Dense(residual.Count, parameterCount) : null;

        for (var imageIndex = 0; imageIndex < imageCount; imageIndex++)
            for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                var (x, y, z) = TransformPoint(state.Rotations[imageIndex],
                    world[imageIndex][pointIndex], state.Translations[imageIndex]);
                var planar = Math.Sqrt(x * x + y * y);
                if (planar < 1e-12) planar = 1e-12;
                var denominator = planar * planar + z * z;
                var theta = Math.Atan2(planar, z);
                var radius = EvaluatePolynomial(state.IncidentPolynomial, theta);
                var radiusDerivative = EvaluatePolynomialDerivative(state.IncidentPolynomial, theta);
                var nx = x / planar; var ny = y / planar;
                var projectedX = state.PrincipalPoint[0] + nx * radius;
                var projectedY = state.PrincipalPoint[1] + ny * radius;
                var row = (imageIndex * pointCount + pointIndex) * 2;
                residual[row] = projectedX - image[imageIndex][pointIndex][0];
                residual[row + 1] = projectedY - image[imageIndex][pointIndex][1];
                if (jacobian is null) continue;

                var thetaX = z * x / (planar * denominator);
                var thetaY = z * y / (planar * denominator);
                var thetaZ = -planar / denominator;
                var planarCubed = planar * planar * planar;
                var dxx = radius * y * y / planarCubed + nx * radiusDerivative * thetaX;
                var dxy = -radius * x * y / planarCubed + nx * radiusDerivative * thetaY;
                var dxz = nx * radiusDerivative * thetaZ;
                var dyx = -radius * x * y / planarCubed + ny * radiusDerivative * thetaX;
                var dyy = radius * x * x / planarCubed + ny * radiusDerivative * thetaY;
                var dyz = ny * radiusDerivative * thetaZ;
                // Expand Dprojection * (-hat(view)) directly: constructing three
                // tiny MathNet matrices for every corner dominated allocation here.
                var poseColumn = imageIndex * 6;
                jacobian[row, poseColumn] = dxx;
                jacobian[row, poseColumn + 1] = dxy;
                jacobian[row, poseColumn + 2] = dxz;
                jacobian[row + 1, poseColumn] = dyx;
                jacobian[row + 1, poseColumn + 1] = dyy;
                jacobian[row + 1, poseColumn + 2] = dyz;
                jacobian[row, poseColumn + 3] = -dxy * z + dxz * y;
                jacobian[row, poseColumn + 4] = dxx * z - dxz * x;
                jacobian[row, poseColumn + 5] = -dxx * y + dxy * x;
                jacobian[row + 1, poseColumn + 3] = -dyy * z + dyz * y;
                jacobian[row + 1, poseColumn + 4] = dyx * z - dyz * x;
                jacobian[row + 1, poseColumn + 5] = -dyx * y + dyy * x;

                var intrinsicColumn = imageCount * 6;
                for (var power = 1; power <= degree; power++)
                {
                    var basis = Math.Pow(theta, power);
                    jacobian[row, intrinsicColumn + power - 1] = nx * basis;
                    jacobian[row + 1, intrinsicColumn + power - 1] = ny * basis;
                }
                jacobian[row, parameterCount - 2] = 1;
                jacobian[row + 1, parameterCount - 1] = 1;
            }

        return new Evaluation(residual, jacobian, 0.5 * residual.DotProduct(residual));
    }

    private static CalibrationState ApplyStep(CalibrationState state, Vector<double> step)
    {
        var rotations = new Matrix<double>[state.Rotations.Length];
        var translations = new Vector<double>[state.Translations.Length];
        for (var index = 0; index < rotations.Length; index++)
        {
            var increment = ExpSe3(step.SubVector(index * 6, 6));
            rotations[index] = increment.Rotation * state.Rotations[index];
            translations[index] = increment.Rotation * state.Translations[index] + increment.Translation;
        }

        var degree = state.IncidentPolynomial.Length - 1;
        var polynomial = state.IncidentPolynomial.ToArray();
        for (var power = 1; power <= degree; power++)
            polynomial[power] += step[state.Rotations.Length * 6 + power - 1];
        var principal = state.PrincipalPoint.Clone();
        principal[0] += step[^2];
        principal[1] += step[^1];
        return new CalibrationState(rotations, translations, polynomial, principal);
    }

    private static FullPose ExpSe3(Vector<double> increment)
    {
        var translationAlgebra = increment.SubVector(0, 3);
        var omega = increment.SubVector(3, 3);
        var angleSquared = omega.DotProduct(omega);
        var omegaHat = Hat(omega);
        var omegaHatSquared = omegaHat * omegaHat;
        double a, b, c;
        if (angleSquared < 1e-12)
        {
            var fourth = angleSquared * angleSquared;
            a = 1 - angleSquared / 6 + fourth / 120;
            b = 0.5 - angleSquared / 24 + fourth / 720;
            c = 1.0 / 6 - angleSquared / 120 + fourth / 5040;
        }
        else
        {
            var angle = Math.Sqrt(angleSquared);
            a = Math.Sin(angle) / angle;
            b = (1 - Math.Cos(angle)) / angleSquared;
            c = (1 - a) / angleSquared;
        }

        var identity = M.DenseIdentity(3);
        var rotation = identity + a * omegaHat + b * omegaHatSquared;
        var velocity = identity + b * omegaHat + c * omegaHatSquared;
        return new FullPose(rotation, velocity * translationAlgebra);
    }

    private static double MeanRadiusToZReprojectionError(
        Vector<double>[] observed,
        Vector<double>[] world,
        Matrix<double> rotation,
        Vector<double> translation,
        double[] polynomial)
    {
        var sum = 0.0;
        for (var index = 0; index < observed.Length; index++)
        {
            var (x, y, z) = TransformPoint(rotation, world[index], translation);
            var planar = Math.Sqrt(x * x + y * y);
            if (planar <= 1e-12) return double.PositiveInfinity;
            var ratio = z / planar;
            var radius = SmallestPositiveRoot(polynomial, ratio);
            if (!double.IsFinite(radius)) return double.PositiveInfinity;
            var projectedX = x / planar * radius;
            var projectedY = y / planar * radius;
            var dx = projectedX - observed[index][0];
            var dy = projectedY - observed[index][1];
            sum += Math.Sqrt(dx * dx + dy * dy);
        }
        return sum / observed.Length;
    }

    private static double SmallestPositiveRoot(double[] polynomial, double linearRatio)
    {
        var coefficients = polynomial.ToArray();
        coefficients[1] -= linearRatio;
        var degree = coefficients.Length - 1;
        while (degree > 0 && Math.Abs(coefficients[degree]) < 1e-30) degree--;
        if (degree == 0) return double.NaN;
        var companion = M.Dense(degree, degree);
        for (var row = 1; row < degree; row++) companion[row, row - 1] = 1;
        for (var row = 0; row < degree; row++) companion[row, degree - 1] = -coefficients[row] / coefficients[degree];
        var roots = companion.Evd().EigenValues;
        var positive = roots.Where(root => Math.Abs(root.Imaginary) <= 1e-7 * (1 + Math.Abs(root.Real)))
            .Select(root => root.Real).Where(root => root > 0).DefaultIfEmpty(double.NaN).Min();
        return positive;
    }

    private static (double X, double Y, double Z) TransformPoint(
        Matrix<double> rotation, Vector<double> point, Vector<double> translation) =>
        (rotation[0, 0] * point[0] + rotation[0, 1] * point[1] + rotation[0, 2] * point[2] + translation[0],
         rotation[1, 0] * point[0] + rotation[1, 1] * point[1] + rotation[1, 2] * point[2] + translation[1],
         rotation[2, 0] * point[0] + rotation[2, 1] * point[1] + rotation[2, 2] * point[2] + translation[2]);

    private static bool CheckOrigin(Matrix<double> rotation, Vector<double> translation) =>
        translation.DotProduct(rotation.Column(2)) > 0;

    private static Matrix<double> GramSchmidt(Matrix<double> firstTwoColumns)
    {
        var first = firstTwoColumns.Column(0);
        first /= first.L2Norm();
        var sourceSecond = firstTwoColumns.Column(1);
        var second = sourceSecond - first * first.DotProduct(sourceSecond);
        second /= second.L2Norm();
        var result = M.Dense(3, 2);
        result.SetColumn(0, first);
        result.SetColumn(1, second);
        return result;
    }

    private static Vector<double> Cross(Vector<double> a, Vector<double> b) => V.DenseOfArray(new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0]
    });

    private static Matrix<double> Hat(Vector<double> vector) => M.DenseOfArray(new[,]
    {
        { 0.0, -vector[2], vector[1] },
        { vector[2], 0.0, -vector[0] },
        { -vector[1], vector[0], 0.0 }
    });

    private static Matrix<double> FlipColumns(Matrix<double> matrix)
    {
        var result = matrix.Clone();
        result.SetColumn(0, matrix.Column(1));
        result.SetColumn(1, matrix.Column(0));
        return result;
    }

    private static Vector<double>[] Center(Vector<double>[] points, Vector<double> principal) =>
        points.Select(point => point - principal).ToArray();

    private static Vector<double> NullVector(Matrix<double> matrix)
    {
        var svd = matrix.Svd(true);
        return svd.VT.Row(svd.VT.RowCount - 1);
    }

    private static Vector<double> SolveLeastSquares(Matrix<double> matrix, Vector<double> target) =>
        matrix.Svd(true).Solve(target);

    private static double SampleStandardDeviation(double[] values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Length - 1));
    }

    private static double EvaluatePolynomial(IReadOnlyList<double> coefficients, double value)
    {
        var result = 0.0;
        for (var index = coefficients.Count - 1; index >= 0; index--) result = result * value + coefficients[index];
        return result;
    }

    private static double EvaluatePolynomialDerivative(IReadOnlyList<double> coefficients, double value)
    {
        var result = 0.0;
        for (var index = coefficients.Count - 1; index >= 1; index--) result = result * value + index * coefficients[index];
        return result;
    }

    private static double StateScale(CalibrationState state)
    {
        var sum = state.Translations.Sum(translation => translation.DotProduct(translation));
        sum += state.IncidentPolynomial.Sum(value => value * value);
        sum += state.PrincipalPoint.DotProduct(state.PrincipalPoint);
        return Math.Sqrt(sum);
    }

    private static double[][] ToRows(Matrix<double> matrix) =>
        Enumerable.Range(0, matrix.RowCount).Select(row => matrix.Row(row).ToArray()).ToArray();

    private static IEnumerable<(int X, int Y)> Spiral(int step, int end)
    {
        if (step <= 0 || end <= 0) { yield return (0, 0); yield break; }
        var x = 0; var y = 0; var maximum = 0; var stop = false;
        while (true)
        {
            maximum += step;
            if (maximum >= end) { maximum = end - 1; stop = true; }
            for (var xi = x; xi <= maximum; xi += step) { yield return (xi, y); x = xi; }
            if (stop) yield break;
            for (var yi = y; yi <= maximum; yi += step) { yield return (x, yi); y = yi; }
            for (var xi = x; xi >= -maximum; xi -= step) { yield return (xi, y); x = xi; }
            for (var yi = y; yi >= -maximum; yi -= step) { yield return (x, yi); y = yi; }
        }
    }

    private static void ValidateInputs(CalibrationObservationDocument observations, OmniCalibrationOptions options)
    {
        if (!Enum.IsDefined(options.Kernel)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown solver kernel.");
        if (options.PolynomialDegree < 2) throw new ArgumentOutOfRangeException(nameof(options), "Polynomial degree must be at least two.");
        if (observations.ImageSizeWidthHeight.Length != 2 || observations.ImageSizeWidthHeight.Any(value => value <= 0))
            throw new InvalidDataException("Image dimensions are missing or invalid.");
        if (observations.Images.Count == 0) throw new InvalidDataException("At least one checkerboard observation is required.");
        if (observations.ObjectPointsXyz.Length < options.PolynomialDegree + 3)
            throw new InvalidDataException("Too few checkerboard points were supplied for the requested polynomial degree.");
        if (observations.ObjectPointsXyz.Any(point => point.Length != 3) ||
            observations.Images.Any(image => image.PointsXy.Length != observations.ObjectPointsXyz.Length ||
                                             image.PointsXy.Any(point => point.Length != 2)))
            throw new InvalidDataException("Observed and object-point arrays have incompatible dimensions.");
    }

    private sealed record PartialPose(Matrix<double> Rotation, Vector<double> Translation);
    private sealed record FullPose(Matrix<double> Rotation, Vector<double> Translation);
    private sealed record FullPoses(Matrix<double>[] Rotations, Vector<double>[] Translations);
    private sealed record PolynomialFit(double[] Polynomial, Vector<double>[] Translations);
    private sealed record InitialCandidate(double MeanError, Matrix<double> Rotation, Vector<double> PartialTranslation);
    private sealed record Evaluation(Vector<double> Residuals, Matrix<double>? Jacobian, double Cost);
    private sealed record OptimizationResult(CalibrationState State, OmniCalibrationDiagnostics Diagnostics);

    private sealed record CalibrationState(
        Matrix<double>[] Rotations,
        Vector<double>[] Translations,
        double[] IncidentPolynomial,
        Vector<double> PrincipalPoint)
    {
        public CalibrationState DeepCopy() => new(
            Rotations.Select(rotation => rotation.Clone()).ToArray(),
            Translations.Select(translation => translation.Clone()).ToArray(),
            IncidentPolynomial.ToArray(),
            PrincipalPoint.Clone());
    }
}


/// <summary>Resources used by the port's worker policy; no instruction-set probing.</summary>
public sealed record PortHardwareCapabilities(
    string OperatingSystem,
    string ProcessArchitecture,
    int LogicalProcessorCount,
    long WorkerMemoryBudgetBytes,
    int RecommendedImageWorkers);
/// <summary>Conservative CPU worker selection; OpenCV performs its own native CPU dispatch.</summary>
public static class PortHardwarePolicy
{
    public const long ReservedMemoryBytes = 512L * 1024 * 1024;
    public const long EstimatedMemoryPerWorkerBytes = 128L * 1024 * 1024;

    // The OS query also works before the first GC and avoids forcing a collection.
    public static long GetMemoryBudget()
    {
        var memory = GC.GetGCMemoryInfo();
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                var physical = (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue);
                return memory.TotalAvailableMemoryBytes > 0
                    ? Math.Min(physical, Math.Max(0, memory.TotalAvailableMemoryBytes - GC.GetTotalMemory(false)))
                    : physical;
            }
        }
        return memory.TotalAvailableMemoryBytes <= 0 || memory.MemoryLoadBytes <= 0
            ? 0 : Math.Max(0, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    public static int RecommendWorkers(int logicalProcessors, long memoryBudgetBytes)
    {
        var processorLimit = Math.Clamp(logicalProcessors, 1, 12);
        // Unknown or insufficient memory still permits one serial worker. This is
        // a scheduling estimate, not a guarantee that arbitrary image sizes fit.
        if (memoryBudgetBytes <= ReservedMemoryBytes) return 1;
        var memoryLimit = (memoryBudgetBytes - ReservedMemoryBytes) / EstimatedMemoryPerWorkerBytes;
        return (int)Math.Clamp(memoryLimit, 1L, processorLimit);
    }

    public static PortHardwareCapabilities Detect()
    {
        var budget = GetMemoryBudget();
        return new PortHardwareCapabilities(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            budget, RecommendWorkers(Environment.ProcessorCount, budget));
    }
}

