namespace SolarShade.Irradiance.Transposition;

/// <summary>Instantaneous transposition with native DNI. Equations and Perez–Driesse spline coefficients
/// follow pvlib-python 0.15.2; see THIRD_PARTY_NOTICES.md. No ground, mask, glazing or electrical losses.</summary>
public static class SkyTransposition
{
    internal const double Radians = Math.PI / 180;
    private static readonly double[] Knots = [0, 0, 0, .061, .187, .333, .487, .643, .778, .839, 1, 1, 1];
    // Columns f11, f12, f13, f21, f22, f23; quadratic B-spline coefficients.
    private static readonly double[,] Coefficients =
    {
        {-.053, .529, -.028, -.071, .061, -.019},
        {-.008, .588, -.062, -.060, .072, -.022},
        {.131, .770, -.167, -.026, .106, -.032},
        {.328, .471, -.216, .069, -.105, -.028},
        {.557, .241, -.300, .086, -.085, -.012},
        {.861, -.323, -.355, .240, -.467, -.008},
        {1.212, -1.239, -.444, .305, -.797, .047},
        {1.099, -1.847, -.365, .275, -1.132, .124},
        {.544, .157, -.213, .118, -1.455, .292},
        {.544, .157, -.213, .118, -1.455, .292}
    };

    /// <summary>All irradiances are W/m². Native DNI is instantaneous, not an hourly mean.
    /// At/under the horizon positive DNI is invalid; positive DHI uses an explicitly flagged isotropic fallback.
    /// Daylight Hay/Perez denominator floors match pvlib (including their near-horizon horizontal bias).</summary>
    public static PlaneIrradiance EvaluateDni(PanelOrientation panel, SunPosition sun,
        double dni, double dhi, double extraterrestrialDni, DiffuseModel model = DiffuseModel.PerezDriesse)
    {
        panel.Validate(); sun.Validate();
        Guard.Irradiance(dni, nameof(dni)); Guard.Irradiance(dhi, nameof(dhi));
        Guard.Range(extraterrestrialDni, double.Epsilon, double.MaxValue, nameof(extraterrestrialDni));
        if (!Enum.IsDefined(model)) throw new ArgumentOutOfRangeException(nameof(model));
        if (sun.ZenithDegrees >= 90 && dni > 0)
            throw new ArgumentException("Positive instantaneous DNI at/under the horizon is inconsistent.", nameof(dni));
        return new PanelKernel(panel).Evaluate(sun, dni, dhi, extraterrestrialDni, model);
    }

    /// <summary>Spencer Earth–Sun distance correction; 1366.1 W/m² solar constant, UTC day-of-year.</summary>
    public static double ExtraterrestrialDni(DateTimeOffset timestamp)
    {
        double b = 2 * Math.PI * (timestamp.UtcDateTime.DayOfYear - 1) / 365;
        return 1366.1 * (1.00011 + .034221 * Math.Cos(b) + .00128 * Math.Sin(b)
            + .000719 * Math.Cos(2 * b) + .000077 * Math.Sin(2 * b));
    }

    private static double Spline(int column, double x)
    {
        int k = 2;
        while (k < 9 && x >= Knots[k + 1]) k++;
        double a0 = (x - Knots[k - 1]) / (Knots[k + 1] - Knots[k - 1]);
        double a1 = (x - Knots[k]) / (Knots[k + 2] - Knots[k]);
        double d0 = (1 - a0) * Coefficients[k - 2, column] + a0 * Coefficients[k - 1, column];
        double d1 = (1 - a1) * Coefficients[k - 1, column] + a1 * Coefficients[k, column];
        double a2 = (x - Knots[k]) / (Knots[k + 1] - Knots[k]);
        return (1 - a2) * d0 + a2 * d1;
    }

    internal readonly struct PanelKernel
    {
        private readonly double sinTilt, cosTilt, azimuth, viewFactor;
        public PanelKernel(PanelOrientation panel)
        {
            (sinTilt, cosTilt) = Math.SinCos(panel.TiltDegrees * Radians);
            azimuth = panel.AzimuthDegrees * Radians;
            viewFactor = (1 + cosTilt) / 2;
        }
        internal PlaneIrradiance Evaluate(SunPosition sun, double dni, double dhi, double extra, DiffuseModel model)
        {
            var components = EvaluateComponents(sun, dni, dhi, extra, model);
            return new(components.Direct, components.SkyDiffuse, components.Flags);
        }
        internal PlaneComponents EvaluateComponents(SunPosition sun, double dni, double dhi, double extra, DiffuseModel model)
        {
            if (sun.ZenithDegrees >= 90)
                return new(0, dhi * viewFactor, 0, 0, dhi > 0 ? TranspositionFlags.NightDiffuseIsotropic : TranspositionFlags.None);
            double z = sun.ZenithDegrees * Radians;
            var (sinZ, cosZ) = Math.SinCos(z);
            double projection = Math.Max(0, cosTilt * cosZ + sinTilt * sinZ * Math.Cos(sun.AzimuthDegrees * Radians - azimuth));
            var flags = sun.ZenithDegrees > 85 ? TranspositionFlags.LowSun : TranspositionFlags.None;
            double isotropic = 0, circumsolar = 0, horizon = 0;
            if (dhi > 0)
            {
                if (model == DiffuseModel.Isotropic) isotropic = dhi * viewFactor;
                else if (model == DiffuseModel.HayDavies)
                {
                    double ai = dni / extra;
                    double rb = projection / Math.Max(cosZ, .01745);
                    isotropic = Math.Max(dhi * (1 - ai) * viewFactor, 0);
                    circumsolar = Math.Max(dhi * ai * rb, 0);
                }
                else
                {
                    double airMass = 1 / (cosZ + .50572 * Math.Pow(96.07995 - sun.ZenithDegrees, -1.6364));
                    double delta = dhi * airMass / extra;
                    double zeta = dni / (dhi + dni);
                    zeta /= 1 - 1.041 * z * z * z * (zeta - 1);
                    double f1 = Math.Clamp(Spline(0, zeta) + Spline(1, zeta) * delta + Spline(2, zeta) * z, 0, .9);
                    double f2 = Spline(3, zeta) + Spline(4, zeta) * delta + Spline(5, zeta) * z;
                    isotropic = dhi * (1 - f1) * viewFactor;
                    circumsolar = dhi * f1 * projection / Math.Max(cosZ, Math.Cos(85 * Radians));
                    horizon = dhi * f2 * sinTilt;
                }
            }
            double beam = dni * projection;
            if (!double.IsFinite(beam) || !double.IsFinite(isotropic + circumsolar + horizon)) throw new ArithmeticException("Transposition overflow.");
            return new(beam, isotropic, circumsolar, horizon, flags);
        }
    }
}

internal readonly record struct PlaneComponents(double Direct, double Isotropic, double Circumsolar, double Horizon, TranspositionFlags Flags)
{
    public double SkyDiffuse => Math.Max(0, Isotropic + Circumsolar + Horizon);
}
