using System.Text.Json;
using SolarShade.Calibration.Solver;
using SolarShade.Calibration.Validation;
using SolarShade.Camera;
using SolarShade.Core.Models;
using Xunit;

namespace SolarShade.Calibration.Tests;

public sealed class CameraProfileFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "camera-profile-tests-" + Guid.NewGuid().ToString("N"));

    private static CalibrationResult Calibration => new(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
        640, 480, [320.25, 240.75], [0, 500.125, -.25, .00125], 65, 238.25, .2, 12, [], DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "fixture");

    [Fact]
    public void ExistingProfileJsonExportsEffectiveNativeParametersAndCoverageOverride()
    {
        Directory.CreateDirectory(directory); string old = Path.Combine(directory, "legacy.json");
        File.WriteAllText(old, JsonSerializer.Serialize(new { Calibration, Source = "fixture", CoverageNote = "provisional" }));
        var profile = CameraProfileFiles.Load(old, coverageOverride: 62);
        var artifacts = CameraProfileFiles.Export(profile, Path.Combine(directory, "output"));
        Assert.Equal(2, artifacts.Count);
        var yaml = CameraProfileFiles.Load(artifacts[0], coverageOverride: 62);
        Assert.Equal(Calibration.PrincipalPoint, yaml.Calibration.PrincipalPoint);
        Assert.Equal(Calibration.IncidentAngleToRadiusPolynomial, yaml.Calibration.IncidentAngleToRadiusPolynomial);
        Assert.Equal(640, yaml.Calibration.ImageWidth);
        var reloaded = CameraProfileFiles.Load(artifacts[1]);
        Assert.Equal(62, reloaded.Calibration.MaximumIncidentAngleDegrees);
        Assert.Contains("override", reloaded.CoverageNote);
        Assert.Equal(Calibration.ImageCircleRadiusPixels, reloaded.Calibration.ImageCircleRadiusPixels);
        Assert.Throws<InvalidDataException>(() => CameraProfileFiles.Load(old, 480, 640));
    }

    [Fact]
    public void FullSolverYamlIsRetainedExactlyAndNeverInfersCoverage()
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "native.yml");
        var solution = new CalibrationReferenceSolution(1, "test", [640, 480], 22, [6, 9],
            Calibration.PrincipalPoint, Calibration.IncidentAngleToRadiusPolynomial, [810.5, 0, -.00025],
            [[[1, 0, 0, 1.25], [0, 1, 0, -2.5], [0, 0, 1, 400.125]]]);
        OmniCalibCSharpPort.WriteYaml(path, solution);
        Assert.Throws<ArgumentException>(() => CameraProfileFiles.Load(path));
        var profile = CameraProfileFiles.Load(path, coverageOverride: 65);
        var output = CameraProfileFiles.Export(profile, Path.Combine(directory, "export"));
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(output[0]));
        Assert.Throws<InvalidDataException>(() => CameraProfileFiles.Load(path, 480, 640, 65));
    }

    [Fact]
    public void CancelledCalibrationAndProfileWritePublishNothing()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CalibrationWorkflow.Calibrate("missing-images", directory, cancellationToken: cts.Token));
        Assert.Throws<OperationCanceledException>(() => CameraProfileFiles.Export(new(Calibration, "fixture", "provisional"), directory, cts.Token));
        Assert.False(Directory.Exists(directory));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
