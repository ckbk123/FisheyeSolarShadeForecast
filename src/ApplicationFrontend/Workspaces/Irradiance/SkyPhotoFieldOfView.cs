using System.Globalization;
using SolarShade.Camera;
using SolarShade.Shading;

namespace SolarShade.Desktop;

/// <summary>Read-only description of the same calibrated angle, image rectangle and lens disk used by projection.</summary>
public sealed record SkyPhotoFieldOfView(double? CalibratedFullDegrees, double? CircularFullDegrees, string Text, string Explanation)
{
    public static SkyPhotoFieldOfView Unavailable(string reason) => new(null, null, "FOV unavailable", reason);

    public static SkyPhotoFieldOfView Load(UserSettings settings, int orientedWidth, int orientedHeight, ImageDisk? selectedDisk)
    {
        if (string.IsNullOrWhiteSpace(settings.ProfilePath)) return Unavailable("Choose a camera profile to read its calibrated field of view.");
        if (orientedWidth < 2 || orientedHeight < 2) return Unavailable("Choose a compatible sky photo to verify the profile dimensions.");
        try
        {
            var profile = CameraProfileFiles.Load(PortablePaths.Resolve(settings.ProfilePath), orientedWidth, orientedHeight,
                settings.CoverageAngle > 0 ? settings.CoverageAngle : null);
            return Describe(profile, orientedWidth, orientedHeight, selectedDisk);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return Unavailable("The selected camera profile cannot be used with this photo: " + ex.Message); }
    }

    public static SkyPhotoFieldOfView Describe(CalibrationProfile profile, int width, int height, ImageDisk? selectedDisk)
    {
        CameraProfileFiles.Validate(profile.Calibration, width, height);
        // Certify the profile with the actual projection validator, including monotonicity.
        _ = new CalibratedSkyProjection(profile.Calibration, imageDisk: selectedDisk);
        var calibration = profile.Calibration;
        double halfAngle = calibration.MaximumIncidentAngleDegrees;
        double fullAngle = 2 * halfAngle;
        string Format(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);
        string text = $"Calibrated FOV: {Format(fullAngle)}° full ({Format(halfAngle)}° half-angle)";
        string explanation = "FOV means field of view. The full angle is twice the maximum incident angle about the optical axis. " +
            "This is the selected profile's calibrated limit, including any saved coverage override; it is not a lens advertising value. " +
            profile.CoverageNote;
        if (selectedDisk == null)
            return new(fullAngle, null, text + " · photo coverage unconfirmed", explanation +
                " Import a mask or update results to establish the photo's lens disk. No segmentation is performed for this readout.");

        // A decentered or cropped disk may have different coverage in different directions.
        // Report the largest complete circular cone around the optical axis, not a diagonal
        // angle that would imply every direction is covered. This does not inspect mask colors.
        double cx = calibration.PrincipalPoint[0], cy = calibration.PrincipalPoint[1];
        double offset = Math.Sqrt(Math.Pow(selectedDisk.CenterX - cx, 2) + Math.Pow(selectedDisk.CenterY - cy, 2));
        double radius = Math.Max(0, Math.Min(selectedDisk.RadiusPixels - offset,
            Math.Min(Math.Min(cx, width - 1 - cx), Math.Min(cy, height - 1 - cy))));
        double Polynomial(double angle)
        {
            double result = 0;
            var polynomial = calibration.IncidentAngleToRadiusPolynomial;
            for (int i = polynomial.Length - 1; i >= 0; i--) result = result * angle + polynomial[i];
            return result;
        }
        double low = 0, high = halfAngle * Math.PI / 180;
        if (Polynomial(high) <= radius) low = high;
        else if (radius > 0)
            for (int i = 0; i < 64; i++)
            {
                double middle = (low + high) / 2;
                if (Polynomial(middle) <= radius) low = middle; else high = middle;
            }
        double circular = 2 * low * 180 / Math.PI;
        text += $" · usable circular FOV: {Format(circular)}°";
        explanation += " Usable circular FOV is the largest full cone centered on the optical axis that fits inside the selected mask's lens disk and image edges, within the calibrated limit. " +
            "A cropped or off-center photo can cover additional directions beyond this complete cone. Black obstructions, camera pose and panel direction do not change this geometric FOV; they affect shading and the panel's observed sky coverage separately.";
        return new(fullAngle, circular, text, explanation);
    }
}
