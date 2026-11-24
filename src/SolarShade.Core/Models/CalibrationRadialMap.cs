namespace SolarShade.Core.Models;

/// <summary>Incident angle (radians) to native radius (pixels). The polynomial is measured up to an optional tangent join.</summary>
public sealed class CalibrationRadialMap
{
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
            start >= calibration.MaximumIncidentAngleDegrees || calibration.MaximumIncidentAngleDegrees > 90)
            throw new ArgumentException("The radial extension needs a positive measured angle below its effective limit of at most 90°.", nameof(calibration));
        joinRadians = start * Math.PI / 180;
        joinRadius = Polynomial(coefficients, joinRadians);
        joinSlope = PolynomialSlope(coefficients, joinRadians);
        if (!double.IsFinite(joinRadius) || joinRadius <= 0 || !double.IsFinite(joinSlope) || joinSlope <= 0)
            throw new ArgumentException("The lens fit must have a finite positive radius and slope at the extension start.", nameof(calibration));
    }

    public double RadiusAt(double radians) => radians > joinRadians
        ? joinRadius + (radians - joinRadians) * joinSlope : Polynomial(coefficients, radians);

    public double SlopeAt(double radians) => radians >= joinRadians ? joinSlope : PolynomialSlope(coefficients, radians);

    /// <summary>Constructs a separate effective profile. An independent image disk still limits pixel coverage.</summary>
    public static CalibrationResult ExtendTo(CalibrationResult measured, double maximumHalfAngleDegrees)
    {
        if (maximumHalfAngleDegrees == measured.MaximumIncidentAngleDegrees) return measured;
        if (measured.LinearExtensionStartDegrees.HasValue || !double.IsFinite(maximumHalfAngleDegrees) ||
            maximumHalfAngleDegrees < measured.MaximumIncidentAngleDegrees || maximumHalfAngleDegrees > 90)
            throw new ArgumentOutOfRangeException(nameof(maximumHalfAngleDegrees), "Extend a measured profile up to a maximum of 90° from the lens axis.");
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
