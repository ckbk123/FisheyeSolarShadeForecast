using System.Text.Json;
using SolarShade.Calibration.Solver;
using SolarShade.Camera;
using SolarShade.Core.Models;

namespace SolarShade.Calibration.Validation;

public sealed record CalibrationStage(CalibrationProfile Profile, OmniCalibRun Run,
    CalibrationValidationReport Validation, IReadOnlyList<string> Artifacts);

/// <summary>Application-facing composition of the unchanged solver and independent validator.</summary>
public static class CalibrationWorkflow
{
    public static CalibrationStage Calibrate(string imageDirectory, string debugDirectory,
        CheckerboardDetectionSettings? settings = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        debugDirectory = Path.GetFullPath(debugDirectory);
        Directory.CreateDirectory(debugDirectory);
        // Native calibration is not interruptible. Stage publication is cancellation-aware and atomic per file.
        string staging = Path.Combine(debugDirectory, ".calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var run = new OmniCalibCSharpPort().Calibrate(imageDirectory, staging, settings);
            cancellationToken.ThrowIfCancellationRequested();
            var report = new OmniCalibrationValidator().Validate(run.Observations, run.Solution.ToReferenceSolution(run.Observations));
            var calibration = run.Solution.ToCalibrationResult(run.Observations, report.Metrics);
            var profile = new CalibrationProfile(calibration, imageDirectory,
                "Provisional maximum angle from fitted checkerboard observations; not independent edge validation.")
                { NativeYaml = File.ReadAllText(run.YamlPath) };
            CameraProfileFiles.Export(profile, staging, cancellationToken);
            CameraProfileFiles.WriteAtomic(Path.Combine(staging, "validation.json"), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            var artifacts = new List<string>();
            foreach (string source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(debugDirectory, Path.GetRelativePath(staging, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, true); artifacts.Add(destination);
            }
            return new(profile, run with { YamlPath = Path.Combine(debugDirectory, "calibration.yml"),
                DebugDirectory = Path.Combine(debugDirectory, "debug") }, report, artifacts.AsReadOnly());
        }
        finally
        {
            // The uniquely named staging directory is created by this invocation under its checked absolute destination.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
