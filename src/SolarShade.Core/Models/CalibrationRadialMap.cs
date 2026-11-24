namespace SolarShade.Core.Models;

/// <summary>Incident angle (radians) to native radius (pixels). The polynomial is measured up to an optional tangent join.</summary>
public sealed class CalibrationRadialMap
{
    public const double MaximumRefinedFullDegrees = 270;
    private readonly double[] coefficients;
    private readonly double joinRadians = double.PositiveInfinity;
    private readonly double joinRadius, joinSlope;
    public IReadOnlyList<double> Coefficients { get; }

    public CalibrationRadialMap(CalibrationResult calibration)
    {
        coefficients = (double[])calibration.IncidentAngleToRadiusPolynomial.Clone();
        Coefficients = Array.AsReadOnly(coefficients);
        if (calibration.LinearExtensionStartDegrees is not double start) return;
        if (!double.IsFinite(start) || start <= 0 || !double.IsFinite(calibration.MaximumIncidentAngleDegrees) ||
            start >= calibration.MaximumIncidentAngleDegrees || calibration.MaximumIncidentAngleDegrees > MaximumRefinedFullDegrees / 2)
            throw new ArgumentException("The radial extension needs a positive measured angle below its effective limit of at most 135° from the lens axis.", nameof(calibration));
        joinRadians = start * Math.PI / 180;
        joinRadius = Polynomial(coefficients, joinRadians);
        joinSlope = PolynomialSlope(coefficients, joinRadians);
        if (!double.IsFinite(joinRadius) || joinRadius <= 0 || !double.IsFinite(joinSlope) || joinSlope <= 0)
            throw new ArgumentException("The lens fit must have a finite positive radius and slope at the extension start.", nameof(calibration));
    }

    public double RadiusAt(double radians) => radians > joinRadians
        ? joinRadius + (radians - joinRadians) * joinSlope : Polynomial(coefficients, radians);

    public double SlopeAt(double radians) => radians >= joinRadians ? joinSlope : PolynomialSlope(coefficients, radians);

    public static CalibrationResult MeasuredProfile(CalibrationResult calibration)
    {
        double? measured = calibration.OriginalMaximumIncidentAngleDegrees ?? calibration.LinearExtensionStartDegrees;
        return measured.HasValue ? calibration with { MaximumIncidentAngleDegrees = measured.Value,
            LinearExtensionStartDegrees = null, OriginalMaximumIncidentAngleDegrees = null } : calibration;
    }

    public static double MaximumApparentFullDegrees(CalibrationResult calibration)
    {
        var measured = MeasuredProfile(calibration);
        double full = 2 * measured.MaximumIncidentAngleDegrees;
        if (full >= MaximumRefinedFullDegrees) return MaximumRefinedFullDegrees;
        double radians = measured.MaximumIncidentAngleDegrees * Math.PI / 180;
        var coefficients = measured.IncidentAngleToRadiusPolynomial;
        double radius = Polynomial(coefficients, radians), slope = PolynomialSlope(coefficients, radians);
        double cap = radius + slope * (MaximumRefinedFullDegrees * Math.PI / 360 - radians);
        return double.IsFinite(radius) && radius > 0 && double.IsFinite(slope) && slope > 0 &&
            double.IsFinite(cap) && cap > radius ? MaximumRefinedFullDegrees : full;
    }

    /// <summary>Use the original polynomial inside the measured field and its endpoint tangent beyond it.</summary>
    public static CalibrationResult WithApparentFullFov(CalibrationResult calibration, double fullDegrees)
    {
        var measured = MeasuredProfile(calibration);
        double limit = MaximumApparentFullDegrees(measured);
        double half = fullDegrees / 2;
        if (!double.IsFinite(fullDegrees) || half <= 0 || fullDegrees > limit)
            throw new ArgumentOutOfRangeException(nameof(fullDegrees), $"Apparent full FOV must be greater than zero and at most {limit:0.###}° for this profile.");
        var effective = half > measured.MaximumIncidentAngleDegrees ? ExtendTo(measured, half) :
            measured with { MaximumIncidentAngleDegrees = half };
        return effective with { OriginalMaximumIncidentAngleDegrees = measured.MaximumIncidentAngleDegrees };
    }

    /// <summary>Constructs a separate effective profile. An independent image disk still limits pixel coverage.</summary>
    public static CalibrationResult ExtendTo(CalibrationResult measured, double maximumHalfAngleDegrees)
    {
        if (maximumHalfAngleDegrees == measured.MaximumIncidentAngleDegrees) return measured;
        if (measured.LinearExtensionStartDegrees.HasValue || !double.IsFinite(maximumHalfAngleDegrees) ||
            maximumHalfAngleDegrees < measured.MaximumIncidentAngleDegrees || maximumHalfAngleDegrees > MaximumRefinedFullDegrees / 2)
            throw new ArgumentOutOfRangeException(nameof(maximumHalfAngleDegrees), "Extend a measured profile up to a maximum of 135° from the lens axis.");
        var effective = measured with { LinearExtensionStartDegrees = measured.MaximumIncidentAngleDegrees,
            MaximumIncidentAngleDegrees = maximumHalfAngleDegrees };
        _ = new CalibrationRadialMap(effective);
        return effective;
    }

    public static double Polynomial(double[] coefficients, double radians)
    {
        double value = 0;
        for (int i = coefficients.Length - 1; i >= 0; i--) value = value * radians + coefficients[i];
        return value;
    }

    private static double PolynomialSlope(double[] coefficients, double radians)
    {
        double slope = 0;
        for (int i = coefficients.Length - 1; i >= 1; i--) slope = slope * radians + i * coefficients[i];
        return slope;
    }
}
