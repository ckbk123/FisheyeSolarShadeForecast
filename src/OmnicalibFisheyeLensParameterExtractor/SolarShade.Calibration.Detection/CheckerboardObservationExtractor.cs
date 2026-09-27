using SolarShade.Core.Models;
using SolarShade.Calibration.Solver;
namespace SolarShade.Calibration.Detection;

/// <summary>Compatibility entry point; implementation lives in OmniCalibCSharpPort.cs.</summary>
public sealed class CheckerboardObservationExtractor
{
    public CalibrationObservationDocument Extract(string imageDirectory, CheckerboardDetectionSettings settings) =>
        new OmniCalibCSharpPort().Detect(imageDirectory, settings);
}
