using System.Text.Json;
using System.Diagnostics;
using SolarShade.Core.Models;
using SolarShade.Calibration.Detection;
using SolarShade.Calibration.Solver;
using SolarShade.Calibration.Validation;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        PrintUsage();
        return 0;
    }

    switch (args[0].ToLowerInvariant())
    {
        case "benchmark-detectors" when args.Length is >= 3 and <= 4:
            {
                var count = Math.Clamp(args.Length == 4 ? int.Parse(args[3]) : 5, 1, 20);
                var defaults = CheckerboardDetectionSettings.CreateFastDefault() with {
                    SearchMode = CheckerboardSearchMode.ClassicFirst, ReadImageOnce = false, SharpenFullResolution = true };
                var variants = new[] {
                    (Name: "reference", Settings: defaults),
                    (Name: "single-read", Settings: defaults with { ReadImageOnce = true }),
                    (Name: "sector-first", Settings: defaults with { ReadImageOnce = true, SearchMode = CheckerboardSearchMode.SectorFirst }),
                    (Name: "sector-fast-first", Settings: defaults with { ReadImageOnce = true, SearchMode = CheckerboardSearchMode.SectorFastFirst }),
                    (Name: "small-sector-first", Settings: defaults with { ReadImageOnce = true, SearchMode = CheckerboardSearchMode.SectorFirst, SharpenFullResolution = false }),
                    (Name: "small-sector-fast", Settings: defaults with { ReadImageOnce = true, SearchMode = CheckerboardSearchMode.SectorFastFirst, SharpenFullResolution = false })
                };
                var port = new OmniCalibCSharpPort();
                var measured = variants.ToDictionary(v => v.Name, _ => new List<PortDetectionProfile>());
                for (var round = -2; round < count; round++)
                    for (var slot = 0; slot < variants.Length; slot++)
                    {
                        var variant = variants[(round + 2 + slot) % variants.Length];
                        var profile = port.ProfileDetection(args[1], variant.Settings);
                        if (round >= 0) measured[variant.Name].Add(profile);
                    }
                var output = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(output);
                var reference = measured["reference"][^1].Observations;
                var reports = variants.Select(variant => {
                    var runs = measured[variant.Name];
                    var observations = runs[^1].Observations;
                    var alignedErrors = new List<double>();
                    var reversedBoards = 0;
                    foreach (var image in observations.Images)
                    {
                        var original = reference.Images.First(i => i.Name == image.Name);
                        double[] Errors(bool reverse) => image.PointsXy.Select((p, j) => {
                            var q = original.PointsXy[reverse ? original.PointsXy.Length - 1 - j : j];
                            return Math.Sqrt((p[0]-q[0])*(p[0]-q[0])+(p[1]-q[1])*(p[1]-q[1]));
                        }).ToArray();
                        var direct = Errors(false); var reversed = Errors(true);
                        var useReverse = reversed.Sum() < direct.Sum();
                        if (useReverse) reversedBoards++;
                        alignedErrors.AddRange(useReverse ? reversed : direct);
                    }
                    var solved = new OmniCalibrator().Solve(observations);
                    var solution = solved.ToReferenceSolution(observations);
                    var validation = new OmniCalibrationValidator().Validate(observations, solution);
                    File.WriteAllText(Path.Combine(output, variant.Name + "-observations.json"), JsonSerializer.Serialize(observations, jsonOptions));
                    File.WriteAllText(Path.Combine(output, variant.Name + "-solution.json"), JsonSerializer.Serialize(solution, jsonOptions));
                    var times = runs.Select(r => r.WallMilliseconds).Order().ToArray();
                    var errors = alignedErrors.Order().ToArray();
                    return new { variant.Name, variant.Settings, boards = observations.Images.Count,
                        median_ms = Percentile(times, 0.5), p95_ms = Percentile(times, 0.95),
                        mean_aligned_corner_change_px = errors.Average(), max_aligned_corner_change_px = errors[^1],
                        p95_aligned_corner_change_px = Percentile(errors, 0.95), reversedBoards,
                        metrics = validation.Metrics,
                        runs = runs.Select(r => new { r.WallMilliseconds, r.Images }) };
                }).ToArray();
                File.WriteAllText(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(new {
                    scope = "detection plus 1000px preview creation; overlapping per-image times are not additive wall time",
                    measured_runs = count, warmups_per_mode = 2, reports
                }, jsonOptions));
                Console.WriteLine(JsonSerializer.Serialize(reports.Select(r => new {
                    r.Name, r.boards, r.median_ms, r.max_aligned_corner_change_px, r.metrics.Rmse2dPixels
                }), jsonOptions));
                return 0;
            }
        case "profile-detection" when args.Length is >= 3 and <= 4:
            {
                var runs = Math.Clamp(args.Length == 4 ? int.Parse(args[3]) : 5, 1, 30);
                var port = new OmniCalibCSharpPort();
                var settings = CheckerboardDetectionSettings.CreateFastDefault();
                _ = port.ProfileDetection(args[1], settings);
                var profiles = Enumerable.Range(0, runs).Select(_ => port.ProfileDetection(args[1], settings)).ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                File.WriteAllText(args[2], JsonSerializer.Serialize(profiles, jsonOptions));
                Console.WriteLine($"Wrote {runs} profiles to {args[2]}; mean wall time {profiles.Average(p => p.WallMilliseconds):F2} ms");
                return 0;
            }
        case "detect" when args.Length is >= 3 and <= 8:
            {
                var fastDefaults = CheckerboardDetectionSettings.CreateFastDefault();
                var settings = new CheckerboardDetectionSettings(
                    args.Length >= 4 ? int.Parse(args[3]) : 6,
                    args.Length >= 5 ? int.Parse(args[4]) : 9,
                    args.Length >= 6 ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 22.0,
                    OpenCvThreadCount: args.Length >= 8 ? int.Parse(args[7]) : fastDefaults.OpenCvThreadCount,
                    ImageWorkerCount: args.Length >= 7 ? int.Parse(args[6]) : fastDefaults.ImageWorkerCount);
                var observations = new CheckerboardObservationExtractor().Extract(args[1], settings);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                File.WriteAllText(args[2], JsonSerializer.Serialize(observations, jsonOptions));
                Console.WriteLine($"Detected {observations.Images.Count} boards; wrote {Path.GetFullPath(args[2])}");
                return 0;
            }
        case "validate" when args.Length is >= 3 and <= 4:
            {
                var observations = Read<CalibrationObservationDocument>(args[1]);
                var solution = Read<CalibrationReferenceSolution>(args[2]);
                var outputDirectory = args.Length == 4 ? args[3] : null;
                var report = new OmniCalibrationValidator().Validate(observations, solution, outputDirectory);
                if (outputDirectory is not null)
                {
                    Directory.CreateDirectory(outputDirectory);
                    File.WriteAllText(Path.Combine(outputDirectory, "validation-report.json"),
                        JsonSerializer.Serialize(report, jsonOptions));
                }
                Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
                return 0;
            }
        case "solve" when args.Length is >= 3 and <= 5:
            {
                var observations = Read<CalibrationObservationDocument>(args[1]);
                var options = new OmniCalibrationOptions(
                    PolynomialDegree: args.Length >= 4 ? int.Parse(args[3]) : 4,
                    MaximumIterations: args.Length >= 5 ? int.Parse(args[4]) : 250);
                var stopwatch = Stopwatch.StartNew();
                var solved = new OmniCalibrator().Solve(observations, options, Console.Error.WriteLine);
                stopwatch.Stop();
                WriteNativeResult(args[2], observations, solved, stopwatch.Elapsed.TotalMilliseconds);
                return 0;
            }
        case "calibrate" when args.Length is >= 3 and <= 7:
            {
                var settings = CheckerboardDetectionSettings.CreateFastDefault() with
                {
                    InnerColumns = args.Length >= 4 ? int.Parse(args[3]) : 6,
                    InnerRows = args.Length >= 5 ? int.Parse(args[4]) : 9,
                    SquareSizeMillimetres = args.Length >= 6
                        ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture)
                        : 22.0
                };
                var degree = args.Length >= 7 ? int.Parse(args[6]) : 4;
                var run = new OmniCalibCSharpPort().Calibrate(args[1], args[2], settings,
                    new OmniCalibrationOptions(PolynomialDegree: degree));
                Console.WriteLine(JsonSerializer.Serialize(new {
                    run.YamlPath, run.DebugDirectory, boards = run.Observations.Images.Count,
                    run.DetectionMilliseconds, run.SolveMilliseconds, run.OutputMilliseconds,
                    run.TotalMilliseconds, target_met = run.TotalMilliseconds < 1000
                }, jsonOptions));
                return 0;
            }
        case "benchmark-pipeline" when args.Length is >= 3 and <= 4:
            {
                var count = Math.Clamp(args.Length == 4 ? int.Parse(args[3]) : 10, 1, 50);
                var port = new OmniCalibCSharpPort();
                var cold = port.Calibrate(args[1], args[2]);
                var runs = new List<object>();
                var totals = new double[count];
                for (var i = 0; i < count; i++)
                {
                    var run = port.Calibrate(args[1], args[2]);
                    totals[i] = run.TotalMilliseconds;
                    runs.Add(new { run.DetectionMilliseconds, run.SolveMilliseconds,
                        run.OutputMilliseconds, run.TotalMilliseconds, boards = run.Observations.Images.Count });
                }
                Array.Sort(totals);
                var result = new {
                    timing_scope = "library call; disk reads, detection, solving, YAML/JSON/CSV/JPEG writes; excludes process startup and benchmark report",
                    warm_cache = true, measured_runs = count, preview_max_dimension = 1000,
                    cold_first_call_ms = cold.TotalMilliseconds,
                    min_ms = totals[0], median_ms = Percentile(totals, 0.5),
                    p95_ms = Percentile(totals, 0.95), max_ms = totals[^1],
                    all_runs_below_one_second = totals.All(t => t < 1000), runs
                };
                var text = JsonSerializer.Serialize(result, jsonOptions);
                File.WriteAllText(Path.Combine(args[2], "benchmark.json"), text);
                Console.WriteLine(text);
                return 0;
            }
        case "benchmark-kernels" when args.Length is >= 3 and <= 4:
            {
                var observations = Read<CalibrationObservationDocument>(args[1]);
                var count = Math.Clamp(args.Length == 4 ? int.Parse(args[3]) : 40, 3, 100);
                var modes = new[] { OmniSolverKernel.MathNet, OmniSolverKernel.Scalar, OmniSolverKernel.Auto };
                var calibrator = new OmniCalibrator();
                var times = modes.ToDictionary(mode => mode, _ => new List<double>());
                var solutions = new Dictionary<OmniSolverKernel, OmniCalibrationSolution>();
                // Interleave warmups and measured runs to reduce order/tiered-JIT bias.
                for (var round = -15; round < count; round++)
                    for (var slot = 0; slot < modes.Length; slot++)
                    {
                        var mode = modes[((round + 15) + slot) % modes.Length];
                        var watch = Stopwatch.StartNew();
                        var solution = calibrator.Solve(observations, new OmniCalibrationOptions(Kernel: mode));
                        var elapsed = watch.Elapsed.TotalMilliseconds;
                        if (round < 0) continue;
                        times[mode].Add(elapsed);
                        solutions[mode] = solution;
                    }
                var reports = modes.Select(mode => {
                    var sorted = times[mode].Order().ToArray();
                    var metrics = new OmniCalibrationValidator().Validate(observations,
                        solutions[mode].ToReferenceSolution(observations)).Metrics;
                    return new { mode = mode.ToString(), implementation = OmniCalibrator.GetKernelDescription(mode),
                        median_ms = Percentile(sorted, 0.5), p95_ms = Percentile(sorted, 0.95),
                        min_ms = sorted[0], max_ms = sorted[^1], metrics, samples_ms = times[mode] };
                }).ToArray();
                var text = JsonSerializer.Serialize(new {
                    scope = "complete solver only; identical frozen observations; interleaved modes; no image I/O",
                    measured_runs_per_mode = count, warmups_per_mode = 15,
                    hardware = PortHardwarePolicy.Detect(), reports
                }, jsonOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
                File.WriteAllText(args[2], text);
                Console.WriteLine(text);
                return 0;
            }
        case "benchmark" when args.Length is >= 2 and <= 4:
            {
                var observations = Read<CalibrationObservationDocument>(args[1]);
                var runCount = Math.Clamp(args.Length >= 3 ? int.Parse(args[2]) : 10, 1, 50);
                var degree = args.Length >= 4 ? int.Parse(args[3]) : 4;
                var options = new OmniCalibrationOptions(PolynomialDegree: degree);
                var calibrator = new OmniCalibrator();
                _ = calibrator.Solve(observations, options); // JIT and library warm-up, not measured.
                var timings = new double[runCount];
                OmniCalibrationSolution? last = null;
                for (var index = 0; index < runCount; index++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    last = calibrator.Solve(observations, options);
                    stopwatch.Stop();
                    timings[index] = stopwatch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(timings);
                var report = new OmniCalibrationValidator().Validate(
                    observations, last!.ToReferenceSolution(observations));
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    hardware = PortHardwarePolicy.Detect(),
                    solver_kernel = OmniCalibrator.GetKernelDescription(),
                    measured_runs = runCount,
                    target_ms = 1000,
                    target_met = timings[(runCount - 1) / 2] < 1000,
                    min_ms = timings[0],
                    median_ms = Percentile(timings, 0.5),
                    p95_ms = Percentile(timings, 0.95),
                    max_ms = timings[^1],
                    metrics = report.Metrics
                }, jsonOptions));
                return 0;
            }
        case "hardware" when args.Length == 1:
            Console.WriteLine(JsonSerializer.Serialize(PortHardwarePolicy.Detect(), jsonOptions));
            return 0;
        default:
            PrintUsage();
            return 2;
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Calibration command failed: {exception.Message}");
    return 1;
}

T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), jsonOptions)
    ?? throw new InvalidDataException($"Could not deserialize {path}.");

void PrintUsage()
{
    Console.WriteLine("SolarShade calibration test endpoint");
    Console.WriteLine("  detect <image-directory> <observations.json> [inner-columns=6] [inner-rows=9] [square-mm=22] [image-workers=auto] [opencv-threads=1]");
    Console.WriteLine("  validate <observations.json> <reference-result.json> [overlay-output-directory]");
    Console.WriteLine("  solve <observations.json> <result.json> [polynomial-degree=4] [maximum-iterations=250]");
    Console.WriteLine("  calibrate <image-directory> <output-directory> [inner-columns=6] [inner-rows=9] [square-mm=22] [polynomial-degree=4]");
    Console.WriteLine("  benchmark <observations.json> [measured-runs=10, max=50] [polynomial-degree=4]");
    Console.WriteLine("  benchmark-kernels <observations.json> <report.json> [runs-per-mode=40, max=100]");
    Console.WriteLine("  benchmark-pipeline <image-directory> <output-directory> [measured-runs=10, max=50] (6 x 9 inner corners, 22 mm)");
    Console.WriteLine("  hardware");
    Console.WriteLine("  profile-detection <image-directory> <report.json> [measured-runs=5]");
    Console.WriteLine("  benchmark-detectors <image-directory> <report-directory> [measured-runs=5]");
}

double Percentile(double[] sorted, double probability)
{
    var position = (sorted.Length - 1) * probability;
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

void WriteNativeResult(
    string resultPath,
    CalibrationObservationDocument observations,
    OmniCalibrationSolution solved,
    double elapsedMilliseconds,
    string? overlayDirectory = null)
{
    var reference = solved.ToReferenceSolution(observations);
    var validation = new OmniCalibrationValidator().Validate(observations, reference, overlayDirectory);
    var document = new NativeCalibrationResultDocument(
        reference.SchemaVersion,
        reference.Reference,
        reference.ImageSizeWidthHeight,
        reference.SquareSizeMillimetres,
        reference.InnerCornersColumnsRows,
        reference.PrincipalPointXy,
        reference.IncidentAngleToRadiusPolynomial,
        reference.RadiusToZPolynomial,
        reference.Extrinsics,
        solved.InitializationImageMask,
        solved.SubsetOptimization,
        solved.FinalOptimization,
        elapsedMilliseconds,
        validation.Metrics,
        validation.PerImage);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
    File.WriteAllText(resultPath, JsonSerializer.Serialize(document, jsonOptions));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result = Path.GetFullPath(resultPath),
        solve_time_ms = elapsedMilliseconds,
        metrics = validation.Metrics,
        diagnostics = solved.FinalOptimization
    }, jsonOptions));
}

