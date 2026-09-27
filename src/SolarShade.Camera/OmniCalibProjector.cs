using SolarShade.Core.Models;

namespace SolarShade.Camera;

public static class OmniCalibProjector
{
    public static bool TryProjectSolarDirection(
        double azimuthDegrees,
        double zenithDegrees,
        CalibrationResult calibration,
        SiteConfiguration site,
        out double pixelX,
        out double pixelY)
    {
        if (calibration.CameraModel != CameraModelKind.OmniCalibIncidentAnglePolynomial)
            throw new NotSupportedException($"Camera model {calibration.CameraModel} is not supported by this projector.");

        // Same world/camera convention as the original astropy_to_camera_extrinsic.py,
        // but retain the 3D camera direction so rays behind the lens are not reflected forward.
        var azimuth = Rad(azimuthDegrees);
        var zenith = Rad(zenithDegrees);
        var heading = Rad(site.CameraHeadingDegrees);
        var tilt = Rad(site.CameraTiltDegrees);
        var thetaGround = 3 * Math.PI / 2 - azimuth;
        var sinZenith = Math.Sin(zenith);
        var xGround = sinZenith * Math.Cos(thetaGround);
        var yGround = sinZenith * Math.Sin(thetaGround);
        var zGround = Math.Cos(zenith);

        var xRotated = Math.Cos(heading) * xGround + Math.Sin(heading) * yGround;
        var yRotated = -Math.Sin(heading) * xGround + Math.Cos(heading) * yGround;
        var xCamera = xRotated;
        var yCamera = Math.Cos(tilt) * yRotated - Math.Sin(tilt) * zGround;
        var zCamera = Math.Sin(tilt) * yRotated + Math.Cos(tilt) * zGround;
        var norm = Math.Sqrt(xCamera * xCamera + yCamera * yCamera + zCamera * zCamera);
        if (norm <= 1e-12) { pixelX = pixelY = 0; return false; }
        var incident = Math.Acos(Math.Clamp(zCamera / norm, -1, 1));
        if (incident * 180.0 / Math.PI > calibration.MaximumIncidentAngleDegrees)
        {
            pixelX = pixelY = 0;
            return false;
        }

        var radialDirection = Math.Sqrt(xCamera * xCamera + yCamera * yCamera);
        var radius = OmniCalibProfileImporter.EvaluatePolynomial(calibration.IncidentAngleToRadiusPolynomial, incident);
        if (radialDirection <= 1e-12)
        {
            pixelX = calibration.PrincipalPoint[0];
            pixelY = calibration.PrincipalPoint[1];
        }
        else
        {
            pixelX = calibration.PrincipalPoint[0] + radius * xCamera / radialDirection;
            pixelY = calibration.PrincipalPoint[1] + radius * yCamera / radialDirection;
        }
        return pixelX >= 0 && pixelX < calibration.ImageWidth && pixelY >= 0 && pixelY < calibration.ImageHeight;
    }

    private static double Rad(double value) => value * Math.PI / 180.0;
}
