namespace SolarShade.Core.Models;

public enum IrradianceSource
{
    NasaPower,
    Pvgis
}

public sealed record SiteConfiguration(
    double Latitude,
    double Longitude,
    double ElevationMetres,
    double PanelAzimuthDegrees,
    double PanelTiltDegrees,
    double CameraHeadingDegrees,
    double CameraTiltDegrees,
    DateOnly StartDate,
    DateOnly EndDate,
    IrradianceSource Source);

public sealed record SolarPosition(double AzimuthDegrees, double ZenithDegrees)
{
    public double ElevationDegrees => 90.0 - ZenithDegrees;
}

public sealed record SourceIrradiance(
    DateTimeOffset TimestampUtc,
    double DirectNormalWm2,
    double DiffuseHorizontalWm2,
    double GlobalHorizontalWm2,
    double? DirectPlaneWm2 = null,
    double? DiffusePlaneWm2 = null,
    double? ReflectedPlaneWm2 = null);

public sealed record IrradianceResult(
    DateTimeOffset TimestampUtc,
    double SolarAzimuthDegrees,
    double SolarZenithDegrees,
    double DirectUnshadedWm2,
    double DiffuseUnshadedWm2,
    double ReflectedUnshadedWm2,
    double DirectVisibility,
    double DiffuseVisibility,
    double UnshadedPlaneWm2,
    double ShadedPlaneWm2)
{
    public double LossWm2 => Math.Max(0, UnshadedPlaneWm2 - ShadedPlaneWm2);
    public double LossPercent => UnshadedPlaneWm2 > 0 ? 100.0 * LossWm2 / UnshadedPlaneWm2 : 0;
}

public enum CameraModelKind
{
    OmniCalibIncidentAnglePolynomial
}

public sealed record CalibrationResult(
    int SchemaVersion,
    CameraModelKind CameraModel,
    int ImageWidth,
    int ImageHeight,
    double[] PrincipalPoint,
    double[] IncidentAngleToRadiusPolynomial,
    double MaximumIncidentAngleDegrees,
    double ImageCircleRadiusPixels,
    double? RmsError,
    int UsedImageCount,
    IReadOnlyList<string> RejectedImages,
    DateTimeOffset CreatedUtc,
    string Algorithm);

public sealed record SkyMask(int Width, int Height, byte[] Pixels)
{
    public bool IsSky(int x, int y) =>
        x >= 0 && x < Width && y >= 0 && y < Height && Pixels[y * Width + x] != 0;
}

public sealed record SegmentationResult(SkyMask Mask, string PreviewPath, double SkyFraction, string Method);

public sealed record EstimationSummary(
    int Samples,
    double UnshadedEnergyKwhM2,
    double ShadedEnergyKwhM2,
    double LossPercent);
