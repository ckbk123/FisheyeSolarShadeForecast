using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Shading;

namespace SolarShade.Desktop;

/// <summary>Read-only description of the same calibrated angle, image rectangle and lens disk used by projection.</summary>
public sealed record SkyPhotoFieldOfView(double? CalibratedFullDegrees, double? CircularFullDegrees, string Text, string Explanation)
{
    /// <summary>Frozen paths in native WPF image coordinates, with pixel (0,0) centered at (0.5,0.5).</summary>
    public Geometry? CalibratedOutline { get; private init; }
    /// <summary>Present when the known photo coverage clips the calibrated cone. A disjoint footprint is empty.</summary>
    public Geometry? UsableOutline { get; private init; }
    public string OutlineLabel { get; private init; } = "FOV unavailable";

    public static SkyPhotoFieldOfView Unavailable(string reason) => new(null, null, "FOV unavailable", reason);

    internal static double CalibratedRadiusPixels(CalibrationResult calibration) =>
        RadiusAt(calibration.IncidentAngleToRadiusPolynomial, calibration.MaximumIncidentAngleDegrees * Math.PI / 180);

    private static double RadiusAt(double[] polynomial, double radians)
    {
        double radius = 0;
        for (int i = polynomial.Length - 1; i >= 0; i--) radius = radius * radians + polynomial[i];
        return radius;
    }

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
        string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
        string text = $"Calibrated FOV: {Format(fullAngle)}° full ({Format(halfAngle)}° half-angle)";
        string legend = $"FOV limit {Format(halfAngle)}° from lens axis · {Format(fullAngle)}° full";
        string explanation = "The solid green circle traces the maximum calibrated incident angle around the optical axis. " +
            "The full field of view is twice that angle. The calculation applies the selected profile and any saved coverage override. " + profile.CoverageNote;
        double cx = calibration.PrincipalPoint[0], cy = calibration.PrincipalPoint[1];
        double low = 0, high = halfAngle * Math.PI / 180;
        double calibratedRadius = CalibratedRadiusPixels(calibration);
        var cone = new EllipseGeometry(new Point(cx + .5, cy + .5), calibratedRadius, calibratedRadius);
        cone.Freeze();
        double nearestPhotoPoint = double.Hypot(cx - Math.Clamp(cx, 0, width - 1), cy - Math.Clamp(cy, 0, height - 1));
        double furthestPhotoPoint = double.Hypot(Math.Max(Math.Abs(cx), Math.Abs(width - 1 - cx)),
            Math.Max(Math.Abs(cy), Math.Abs(height - 1 - cy)));
        if (calibratedRadius < nearestPhotoPoint || calibratedRadius > furthestPhotoPoint)
        {
            text += " · calibrated edge outside photo";
            legend += " · calibrated edge outside photo";
        }
        if (selectedDisk == null)
            return new(fullAngle, null, text + " · photo coverage unconfirmed", explanation +
                " Import a mask or update results to establish the photo's lens disk.")
            { CalibratedOutline = cone, OutlineLabel = legend + " · photo coverage unconfirmed" };

        // The nearest edge bounds the largest complete cone. More distant edges can still provide
        // usable rays, so the displayed footprint uses the actual intersection in pixel coordinates.
        double offset = double.Hypot(selectedDisk.CenterX - cx, selectedDisk.CenterY - cy);
        double radius = Math.Max(0, Math.Min(selectedDisk.RadiusPixels - offset,
            Math.Min(Math.Min(cx, width - 1 - cx), Math.Min(cy, height - 1 - cy))));
        bool completeConeFits = calibratedRadius <= radius;
        if (completeConeFits) low = high;
        else if (radius > 0)
            for (int i = 0; i < 64; i++)
            {
                double middle = (low + high) / 2;
                if (RadiusAt(calibration.IncidentAngleToRadiusPolynomial, middle) <= radius) low = middle; else high = middle;
            }
        double circular = 2 * low * 180 / Math.PI;
        text += $" · usable circular FOV: {Format(circular)}°";
        explanation += " The usable circular FOV is the largest complete cone around the lens axis that fits within the photo's lens disk and image edges.";
        Geometry? usable = null;
        if (!completeConeFits)
        {
            var disk = new EllipseGeometry(new Point(selectedDisk.CenterX + .5, selectedDisk.CenterY + .5),
                selectedDisk.RadiusPixels, selectedDisk.RadiusPixels);
            // TryProject accepts pixel centers from 0 through Width-1 and Height-1, inclusively.
            var image = new RectangleGeometry(new Rect(.5, .5, width - 1, height - 1));
            // Materialize the clipped vector path once, independently of later preview zoom or pan.
            const double tolerancePixels = .01;
            var clippedDisk = Geometry.Combine(disk, image, GeometryCombineMode.Intersect, null, tolerancePixels, ToleranceType.Absolute);
            usable = Geometry.Combine(cone, clippedDisk, GeometryCombineMode.Intersect, null, tolerancePixels, ToleranceType.Absolute);
            usable.Freeze();
            string edge = usable.IsEmpty() ? " · no usable area on this photo" : " · dashed usable photo boundary";
            legend += edge;
            text += edge;
            explanation += " The dashed boundary traces the intersection of the calibrated cone, photo lens disk and image rectangle. Its angle can vary around a cropped or off-center photo.";
        }
        return new(fullAngle, circular, text, explanation) { CalibratedOutline = cone, UsableOutline = usable, OutlineLabel = legend };
    }
}
