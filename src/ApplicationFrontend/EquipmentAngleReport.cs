using System.Globalization;

namespace SolarShade.Desktop;

/// <summary>Public-facing angles of the saved physical pose. Property names carry the units and reference.</summary>
public sealed record EquipmentAngleReport(
    double? PhoneLensAzimuthClockwiseFromSouthDegrees,
    double? PhoneTiltFromHorizontalDegrees,
    double LensZenithFromUpDegrees,
    double? ImageTopBearingClockwiseFromTrueNorthDegrees,
    double SourceProjectionRollDegrees,
    string PhonePlacement,
    bool IsVincentPhonePose,
    double? PanelFrontAzimuthClockwiseFromSouthDegrees,
    double PanelTiltFromHorizontalDegrees)
{
    public string Convention => "Vincent Boitier: equipment azimuth clockwise from geographic south (west +90°, east -90°); flat planes have undefined azimuth.";

    internal static EquipmentAngleReport? From(UserSettings settings)
    {
        // Calibration and interrupted runs can have incomplete study settings.
        if (!double.IsFinite(settings.BottomAzimuth) || !double.IsFinite(settings.CameraTilt) ||
            Math.Abs(settings.CameraTilt) > 180 || !double.IsFinite(settings.CameraRoll) ||
            !double.IsFinite(settings.PanelAzimuth) || !double.IsFinite(settings.PanelTilt) ||
            settings.PanelTilt is < 0 or > 90)
            return null;

        var angles = EquipmentOrientation.Describe(settings);
        return new(angles.PhoneAzimuth, angles.PhoneTilt <= 90 ? angles.PhoneTilt : null,
            angles.PhoneTilt, angles.ImageTopBearing, angles.Roll, angles.PhonePlacement,
            angles.IsVincentPhonePose, angles.PanelAzimuth, angles.PanelTilt);
    }

    private static string Degrees(double? value) =>
        value is { } n ? n.ToString("0.###", CultureInfo.InvariantCulture) + "°" : "undefined";

    internal string DescribePhone()
    {
        string tilt = PhoneTiltFromHorizontalDegrees is { } t
            ? "tilt " + Degrees(t) + " from horizontal"
            : "lens zenith " + Degrees(LensZenithFromUpDegrees) + " (outside 0–90°)";
        return $"Lens azimuth {Degrees(PhoneLensAzimuthClockwiseFromSouthDegrees)} (S = 0°) | {tilt}";
    }

    internal string DescribeImage() =>
        $"Image top {Degrees(ImageTopBearingClockwiseFromTrueNorthDegrees)} (true N = 0°) | source camera roll {Degrees(SourceProjectionRollDegrees)} | {PhonePlacement}";

    internal string DescribePanel() =>
        $"Front azimuth {Degrees(PanelFrontAzimuthClockwiseFromSouthDegrees)} (S = 0°) | tilt {Degrees(PanelTiltFromHorizontalDegrees)} from horizontal";
}
