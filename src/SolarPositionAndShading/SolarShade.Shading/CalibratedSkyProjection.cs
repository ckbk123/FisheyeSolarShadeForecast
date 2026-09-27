using SolarShade.Core.Models;

namespace SolarShade.Shading;

/// <summary>
/// Immutable calibrated world-ray projection into EXIF-oriented image coordinates.
/// Shares the shading projection and coverage rules without decoding a mask, sampling a sky dome,
/// or preparing visibility. Calibration arrays are copied so caller mutation cannot change the projection.
/// </summary>
public sealed class CalibratedSkyProjection
{
    private readonly double[] poly;
    private readonly double cx, cy, minZ;
    private readonly Direction ax, ay, az;
    private readonly ImageDisk disk;

    public int Width { get; }
    public int Height { get; }
    /// <summary>Camera pose used for this immutable projection, in oriented-image coordinates.</summary>
    public CameraPose Pose { get; }
    internal double MaximumIncidentAngleRadians { get; }
    internal double LipschitzBound { get; }
    internal Direction OpticalAxis => az;
    internal ImageDisk CoverageDisk => disk;
    internal double PrincipalPointX => cx;
    internal double PrincipalPointY => cy;
    internal IReadOnlyList<double> IncidentAnglePolynomial => Array.AsReadOnly(poly);

    public CalibratedSkyProjection(CalibrationResult calibration, CameraPose? pose = null, ImageDisk? imageDisk = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (calibration.ImageWidth < 2 || calibration.ImageHeight < 2)
            throw new ArgumentException("Calibration must describe valid EXIF-oriented image dimensions.", nameof(calibration));
        if (calibration.CameraModel != CameraModelKind.OmniCalibIncidentAnglePolynomial ||
            calibration.PrincipalPoint.Length != 2 || calibration.PrincipalPoint.Any(v => !double.IsFinite(v)) ||
            calibration.IncidentAngleToRadiusPolynomial.Length < 2 || calibration.IncidentAngleToRadiusPolynomial.Any(v => !double.IsFinite(v)) ||
            !double.IsFinite(calibration.MaximumIncidentAngleDegrees) || calibration.MaximumIncidentAngleDegrees is <= 0 or > 180)
            throw new ArgumentException("A finite, validated OmniCalib angle-to-radius profile is required.");
        // A nonzero intercept assigns an azimuth-dependent radius to the optical axis and is not continuous.
        if (Math.Abs(calibration.IncidentAngleToRadiusPolynomial[0]) > 1e-9)
            throw new ArgumentException("The incident-angle polynomial must have zero constant coefficient.");
        Width = calibration.ImageWidth; Height = calibration.ImageHeight;
        poly = (double[])calibration.IncidentAngleToRadiusPolynomial.Clone();
        cx = calibration.PrincipalPoint[0]; cy = calibration.PrincipalPoint[1];
        disk = imageDisk ?? new(cx, cy, calibration.ImageCircleRadiusPixels);
        if (!double.IsFinite(disk.CenterX) || !double.IsFinite(disk.CenterY) || !double.IsFinite(disk.RadiusPixels) || disk.RadiusPixels <= 0)
            throw new ArgumentException("A valid image coverage disk is required.");
        var maxAngle = calibration.MaximumIncidentAngleDegrees*Math.PI/180;
        MaximumIncidentAngleRadians = maxAngle;
        minZ = Math.Cos(maxAngle);
        // Conservative analytic Lipschitz bound over [0,maxAngle].
        // theta/sin(theta) increases on [0,pi); triangle inequality bounds all polynomial terms.
        // This can overestimate footprint size but must never underestimate it.
        double lipschitz=0;
        for(int interval=0;interval<128;interval++)
        {
            double low=maxAngle*interval/128,high=maxAngle*(interval+1)/128,derivativeBound=0,derivativeLower=0,radialBound=0;
            for(int j=1;j<poly.Length;j++)
            {
                var endpoint=poly[j]>=0?high:low;
                derivativeBound+=j*poly[j]*Math.Pow(endpoint,j-1);
                derivativeLower+=j*poly[j]*Math.Pow(poly[j]>=0?low:high,j-1);
                radialBound+=poly[j]*Math.Pow(endpoint,j-1);
            }
            if(!double.IsFinite(derivativeBound)||!double.IsFinite(radialBound)||derivativeLower<=0)
                throw new ArgumentException("Projection monotonicity cannot be certified over the requested range; reduce the validated angle or revise the profile.");
            radialBound*=high/Math.Max(1e-15,Math.Sin(high));
            lipschitz=Math.Max(lipschitz,Math.Max(derivativeBound,radialBound));
        }
        LipschitzBound = lipschitz;
        for (int i = 0; i <= 2048; i++)
        {
            var a = maxAngle*i/2048;
            double derivative = 0;
            for (int j = poly.Length-1; j >= 1; j--) derivative = derivative*a + j*poly[j];
            if (derivative <= 0 || Polynomial(a) < 0)
                throw new ArgumentException("Projection must remain monotonic throughout its validated angular range; reduce the range, do not fold rays.");
        }
        Pose = pose ?? new();
        (ax, ay, az) = Pose.Axes();
    }

    /// <summary>
    /// Projects a unit world ray without perspective division. Returns false outside calibrated angle,
    /// image rectangle or actual image disk. Horizon eligibility is handled separately by the caller.
    /// </summary>
    public bool TryProject(Direction ray, out double x, out double y)
    {
        var z = ray.Dot(az);
        if (!double.IsFinite(z) || z < minZ) { x = y = 0; return false; }
        var a = ray.Dot(ax); var b = ray.Dot(ay);
        var lateral = Math.Sqrt(a*a+b*b);
        if(z<0&&lateral<1e-12){x=y=0;return false;} // antipodal optical axis has no unique image azimuth
        var radius = Polynomial(Math.Atan2(lateral, z));
        x = cx + (lateral < 1e-14 ? 0 : radius*a/lateral);
        y = cy + (lateral < 1e-14 ? 0 : radius*b/lateral);
        return x >= 0 && x <= Width-1 && y >= 0 && y <= Height-1 &&
            (x-disk.CenterX)*(x-disk.CenterX)+(y-disk.CenterY)*(y-disk.CenterY) <= disk.RadiusPixels*disk.RadiusPixels;
    }

    /// <summary>
    /// Tests whether two successfully projected finite unit rays can be joined along their shortest spherical arc
    /// without crossing the excluded rear calibration cap or the optical antipode's undefined image azimuth.
    /// This checks connectivity of existing rays; it does not generate a solar position or add weather samples.
    /// Endpoints must already have passed TryProject. A cone of at most 90 degrees is geodesically convex.
    /// </summary>
    public bool CanConnectProjectedRays(Direction first, Direction second)
    {
        if (MaximumIncidentAngleRadians <= Math.PI / 2) return true;
        double cosine = Math.Clamp(first.Dot(second), -1, 1);
        var cross = first.Cross(second);
        double sine = Math.Sqrt(cross.Dot(cross));
        if (sine < 1e-15) return cosine > 0; // equal rays connect; opposite rays have no unique shortest arc

        double a = first.Dot(az), endZ = second.Dot(az);
        // Along the shortest arc z(t)=a*cos(t)+b*sin(t), 0<=t<=theta<=pi.
        // An interior minimum exists exactly when the initial derivative is negative and the final derivative positive.
        // Their numerators avoid inverse trigonometry; the analytic minimum is -sqrt(a*a+b*b).
        double initialDerivativeNumerator = endZ - cosine * a;
        double finalDerivativeNumerator = cosine * endZ - a;
        double minimumZ = Math.Min(a, endZ);
        if (initialDerivativeNumerator < 0 && finalDerivativeNumerator > 0)
        {
            double b = initialDerivativeNumerator / sine;
            minimumZ = -Math.Sqrt(Math.Clamp(a*a + b*b, 0, 1));
        }
        // Keep a tiny conservative numerical guard at the singularity even for exactly 180-degree calibration.
        return minimumZ >= minZ && minimumZ > -1 + 1e-14;
    }

    private double Polynomial(double theta)
    {
        double value = 0;
        for (int i = poly.Length-1; i >= 0; i--) value = value*theta+poly[i];
        return value;
    }
}
