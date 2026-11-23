using System.Text.Json.Serialization;
using SolarShade.Shading;

namespace SolarShade.Desktop;

/// <summary>Equipment azimuths are clockwise from geographic south. Image-top bearing is clockwise from geographic north.</summary>
public sealed record EquipmentAngles(
    double? PhoneAzimuth, double PhoneTilt, double? PanelAzimuth, double PanelTilt,
    double? ImageTopBearing, double Roll, bool IsVincentPhonePose, string PhonePlacement);

/// <summary>
/// Interprets the persisted north-bearing camera/receiver parameters without modifying a saved pose.
/// The camera and transposition libraries continue to share the solar ephemeris's geographic frame.
/// </summary>
public static class EquipmentOrientation
{
    internal static double Wrap360(double angle)
    {
        double value = angle % 360;
        if (value < 0) value += 360;
        return value == 360 || value == 0 ? 0 : value;
    }

    internal static double FromNorth(double bearing)
    {
        double signed = Wrap360(Wrap360(bearing) - 180);
        return signed > 180 ? signed - 360 : signed;
    }

    internal static double ToNorth(double azimuth) => Wrap360(Wrap360(azimuth) + 180);

    internal static bool SameBearing(double a, double b)
    {
        double difference = Wrap360(Wrap360(a) - Wrap360(b));
        return difference < 1e-11 || 360 - difference < 1e-11;
    }

    internal static void RequireFinite(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Enter a finite angle.");
    }

    public static CameraPose ToCameraPose(UserSettings settings) =>
        CameraPose.FromImageBottom(settings.BottomAzimuth, settings.CameraTilt, settings.CameraRoll);

    private static double? HorizontalBearing(Direction vector) =>
        Math.Abs(vector.East) < 1e-14 && Math.Abs(vector.North) < 1e-14
            ? null : Wrap360(Math.Atan2(vector.East, vector.North) * 180 / Math.PI);

    // The unrolled tilt points toward image top for positive legacy tilt and toward image bottom for negative tilt.
    internal static double FacingBearing(UserSettings settings) =>
        Wrap360(Wrap360(settings.BottomAzimuth) + (settings.CameraTilt > 0 ? 180 : 0));

    public static EquipmentAngles Describe(UserSettings settings)
    {
        double tilt = Math.Abs(settings.CameraTilt);
        double? panelAzimuth = settings.PanelTilt == 0 || !double.IsFinite(settings.PanelAzimuth)
            ? null : FromNorth(settings.PanelAzimuth);
        if (!HasValidCameraFrame(settings))
            return new(null, tilt, panelAzimuth, settings.PanelTilt, null, settings.CameraRoll, false, "Invalid camera pose");

        var (_, down, _) = ToCameraPose(settings).Axes();
        double? top = HorizontalBearing(-1 * down);
        double? phoneAzimuth = tilt is 0 or 180 ? null : FromNorth(FacingBearing(settings));
        // Compare the uphill component to the greatest possible rise of a unit edge in this plane.
        double riseTolerance = 1e-12 * Math.Sin(tilt * Math.PI / 180);
        bool uphill = -down.Up > riseTolerance;
        string placement = tilt switch
        {
            0 => "Flat",
            > 90 => "Lens points below the horizon",
            _ when uphill => "Lens end uphill",
            _ when down.Up > riseTolerance => "Lens end downhill",
            _ => "Lens end level"
        };
        return new(phoneAzimuth, tilt, panelAzimuth, settings.PanelTilt, top, settings.CameraRoll,
            tilt == 0 || (tilt <= 90 && uphill), placement);
    }

    internal static double HeadingWhenFlattened(UserSettings settings, EquipmentAngles angles)
    {
        if (angles.ImageTopBearing is double heading) return heading;
        if (!HasValidCameraFrame(settings)) return 0;
        // With image top vertical, image right still fixes the yaw of the photograph.
        var (right, _, _) = ToCameraPose(settings).Axes();
        return Wrap360(HorizontalBearing(right)!.Value + 90);
    }

    private static bool HasValidCameraFrame(UserSettings settings) =>
        double.IsFinite(settings.BottomAzimuth) && double.IsFinite(settings.CameraRoll) && Math.Abs(settings.CameraTilt) <= 180;

    /// <summary>Matches the lens axis to the panel front. A horizontal panel leaves the photo's heading independent.</summary>
    public static void PlacePhoneOnPanel(UserSettings settings)
    {
        if (!(settings.PanelTilt >= 0 && settings.PanelTilt <= 90) || !double.IsFinite(settings.PanelAzimuth))
            throw new ArgumentOutOfRangeException(nameof(settings), "Enter a valid panel orientation before placing the phone.");
        double bottom = settings.PanelTilt == 0
            ? Wrap360(HeadingWhenFlattened(settings, Describe(settings)) + 180)
            : Wrap360(settings.PanelAzimuth);
        settings.BottomAzimuth = bottom;
        settings.CameraTilt = settings.PanelTilt == 0 ? 0 : -settings.PanelTilt;
        settings.CameraRoll = 0;
    }

    /// <summary>Explicitly adopts a lens-end-uphill placement, keeping the outward bearing if it exists.</summary>
    public static void UseUphillPlacement(UserSettings settings)
    {
        var angles = Describe(settings);
        double facing = angles.PhoneAzimuth is double azimuth ? ToNorth(azimuth) :
            double.IsFinite(settings.BottomAzimuth) ? Wrap360(settings.BottomAzimuth) : 180;
        if (angles.PhoneTilt == 0 && angles.ImageTopBearing is double top) facing = Wrap360(top + 180);
        settings.BottomAzimuth = facing;
        settings.CameraTilt = double.IsFinite(angles.PhoneTilt) && angles.PhoneTilt > 0 ? -Math.Min(angles.PhoneTilt, 90) : 0;
        settings.CameraRoll = 0;
    }
}

public sealed partial record UserSettings
{
    // These editing properties must never be deserialized alongside the original physical inputs.
    // Older settings, input fingerprints, and study snapshots all use those original fields.
    [JsonIgnore]
    public double PanelFacingAzimuth
    {
        get => EquipmentOrientation.FromNorth(PanelAzimuth);
        set
        {
            EquipmentOrientation.RequireFinite(value);
            double north = EquipmentOrientation.ToNorth(value);
            if (!(PanelAzimuth >= 0 && PanelAzimuth <= 360) || !EquipmentOrientation.SameBearing(north, PanelAzimuth))
                PanelAzimuth = north;
        }
    }

    [JsonIgnore]
    public double PhoneFacingAzimuth
    {
        get => EquipmentOrientation.FromNorth(EquipmentOrientation.FacingBearing(this));
        set
        {
            EquipmentOrientation.RequireFinite(value);
            var angles = EquipmentOrientation.Describe(this);
            if (angles.PhoneAzimuth.HasValue && EquipmentOrientation.SameBearing(value, angles.PhoneAzimuth.Value)) return;
            if (!angles.IsVincentPhonePose || !angles.PhoneAzimuth.HasValue)
                throw new InvalidOperationException("Phone azimuth requires a tilted phone with its lens end uphill.");
            double trueBearing = EquipmentOrientation.ToNorth(value);
            BottomAzimuth = EquipmentOrientation.Wrap360(trueBearing + (CameraTilt > 0 ? 180 : 0));
        }
    }

    [JsonIgnore]
    public double PhoneInclination
    {
        get => Math.Abs(CameraTilt);
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 90)
                throw new ArgumentOutOfRangeException(nameof(value), "Phone tilt must be between 0° and 90°.");
            if (value == PhoneInclination) return;
            var angles = EquipmentOrientation.Describe(this);
            if (!angles.IsVincentPhonePose)
                throw new InvalidOperationException("Choose the uphill placement before editing this legacy pose.");
            if (value == 0 || CameraTilt == 0)
            {
                // A flat lens has no horizontal direction. Its image heading survives a transition
                // to/from flat, including a photo whose old yaw was split between heading and roll.
                if (CameraRoll != 0 || CameraTilt > 0)
                    BottomAzimuth = EquipmentOrientation.Wrap360(EquipmentOrientation.HeadingWhenFlattened(this, angles) + 180);
                CameraRoll = 0;
                CameraTilt = value == 0 ? 0 : -value;
                return;
            }
            CameraTilt = Math.CopySign(value, CameraTilt);
        }
    }

    [JsonIgnore]
    public double FlatImageTopBearing
    {
        get => EquipmentOrientation.Describe(this).ImageTopBearing ?? EquipmentOrientation.Wrap360(BottomAzimuth + 180);
        set
        {
            EquipmentOrientation.RequireFinite(value);
            if (CameraTilt != 0)
                throw new InvalidOperationException("Independent image heading is available when the phone is flat.");
            double current = FlatImageTopBearing;
            if (!EquipmentOrientation.SameBearing(current, value))
                BottomAzimuth = EquipmentOrientation.Wrap360(EquipmentOrientation.Wrap360(BottomAzimuth)
                    + EquipmentOrientation.Wrap360(value) - current);
        }
    }
}
