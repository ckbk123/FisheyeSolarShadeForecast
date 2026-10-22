namespace SolarShade.Calibration.Validation;

/// <summary>A calibration failure with retained per-photo feedback for user review.</summary>
public sealed class CalibrationReviewException(string message, string diagnosticsDirectory, Exception innerException)
    : IOException(message, innerException)
{
    public string DiagnosticsDirectory { get; } = diagnosticsDirectory;
}
