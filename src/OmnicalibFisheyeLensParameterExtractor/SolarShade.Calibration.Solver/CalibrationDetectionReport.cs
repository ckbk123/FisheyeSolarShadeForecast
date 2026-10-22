namespace SolarShade.Calibration.Solver;

/// <summary>Per-photo detector feedback, retained even when calibration cannot be solved.</summary>
public sealed record CalibrationImageDetection(string Name, string Path, bool Detected,
    int CornerCount, string Message, string? PreviewFile);

public sealed record CalibrationDetectionReport(int SchemaVersion, int InnerColumns, int InnerRows,
    IReadOnlyList<CalibrationImageDetection> Images, string? Failure = null);
