using SolarShade.Irradiance;

namespace SolarShade.Shading;

/// <summary>Geodetic coordinates. Elevation in metres, longitude positive east.</summary>
public readonly record struct SolarSite(double LatitudeDegrees, double LongitudeDegrees, double ElevationMetres)
{
    internal void Validate()
    {
        if (!double.IsFinite(LatitudeDegrees) || Math.Abs(LatitudeDegrees) > 90 ||
            !double.IsFinite(LongitudeDegrees) || Math.Abs(LongitudeDegrees) > 180 ||
            !double.IsFinite(ElevationMetres) || ElevationMetres is < -1000 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(SolarSite));
    }
}

public readonly record struct Direction(double East, double North, double Up)
{
    public static Direction FromAngles(double azimuthDegrees, double zenithDegrees)
    {
        var (sa, ca) = Math.SinCos(azimuthDegrees * Math.PI / 180);
        var (sz, cz) = Math.SinCos(zenithDegrees * Math.PI / 180);
        return new(sz * sa, sz * ca, cz);
    }
    public double Dot(Direction b) => East * b.East + North * b.North + Up * b.Up;
    public Direction Unit() { var n = Math.Sqrt(Dot(this)); return new(East / n, North / n, Up / n); }
    public Direction Cross(Direction b) => new(North*b.Up-Up*b.North, Up*b.East-East*b.Up, East*b.North-North*b.East);
    public static Direction operator +(Direction a, Direction b) => new(a.East+b.East, a.North+b.North, a.Up+b.Up);
    public static Direction operator *(double a, Direction b) => new(a*b.East, a*b.North, a*b.Up);
}

public readonly record struct SolarAngles(double AzimuthDegrees, double ZenithDegrees)
{
    public Direction Direction => Direction.FromAngles(AzimuthDegrees, ZenithDegrees);
}
public readonly record struct SolarPositionRow(DateTimeOffset Timestamp, SolarAngles Position);

/// <summary>Explicit averaging window relative to each provider label. No inferred phase shifts.</summary>
public sealed record TimeSampling(double StartOffsetMinutes, double DurationMinutes, int Samples)
{
    public static TimeSampling Instant { get; } = new(0, 0, 1);
    public static TimeSampling PrecedingHour(int samples = 60) => new(-60, 60, samples);
    public static TimeSampling FollowingHour(int samples = 60) => new(0, 60, samples);
    public static TimeSampling CenteredHour(int samples = 60) => new(-30, 60, samples);
    /// <summary>Verified native-provider conventions. null means caller must explicitly choose a window.</summary>
    public static TimeSampling? ForService(IrradianceService service, int samples = 60) => service switch
    {
        IrradianceService.NasaPower => FollowingHour(samples), // https://power.larc.nasa.gov/docs/faqs/other/
        IrradianceService.OpenMeteo or IrradianceService.Cams or IrradianceService.CopernicusCds => PrecedingHour(samples),
        _ => null
    };
    internal void Validate()
    {
        if (!double.IsFinite(StartOffsetMinutes) || Math.Abs(StartOffsetMinutes) > 1440 ||
            !double.IsFinite(DurationMinutes) || DurationMinutes is < 0 or > 1440 || Samples is < 1 or > 10000 ||
            (DurationMinutes == 0 && Samples != 1)) throw new ArgumentOutOfRangeException(nameof(TimeSampling));
    }
}

public sealed record SolarRow(DateTimeOffset Timestamp, double DirectHorizontalWm2,
    SolarAngles? AtLabel, IReadOnlyList<Direction> IntegrationDirections);
public sealed record SolarSequence(SolarSite Site, TimeSampling Sampling, IReadOnlyList<SolarRow> Rows);

/// <summary>Module 1: topocentric solar positions, independent of camera/mask and file I/O.</summary>
public sealed partial class SolarPositionModule
{
    private readonly SolarSite site;
    public SolarPositionModule(SolarSite site)
    {
        site.Validate(); this.site = site;
    }

    /// <summary>NOAA/Meeus solar equations with observer parallax. No atmospheric refraction.
    /// UTC approximates UT1, as in NOAA's calculator; not the full Meeus ephemeris series.</summary>
    public SolarAngles Calculate(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        if (utc.Year is < 1900 or > 2100)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Supported UTC range: 1900–2100.");
        const double rad = Math.PI / 180;
        // NOAA reference: https://gml.noaa.gov/grad/solcalc/main.js
        // Julian centuries from J2000, preserving fractional seconds without Excel date approximations.
        double t = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays / 36525;
        double l = Normalize(280.46646 + t * (36000.76983 + 0.0003032 * t)) * rad;
        double m = (357.52911 + t * (35999.05029 - 0.0001537 * t)) * rad;
        double e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        double c = Math.Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t))
                 + Math.Sin(2 * m) * (0.019993 - 0.000101 * t) + Math.Sin(3 * m) * 0.000289;
        double omega = (125.04 - 1934.136 * t) * rad;
        double lambda = l + (c - 0.00569 - 0.00478 * Math.Sin(omega)) * rad;
        double epsilon = (23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - 0.001813 * t))) / 60) / 60
                          + 0.00256 * Math.Cos(omega)) * rad;
        double declination = Math.Asin(Math.Sin(epsilon) * Math.Sin(lambda));
        double y = Math.Pow(Math.Tan(epsilon / 2), 2);
        double equationMinutes = 4 / rad * (y * Math.Sin(2 * l) - 2 * e * Math.Sin(m)
            + 4 * e * y * Math.Sin(m) * Math.Cos(2 * l) - 0.5 * y * y * Math.Sin(4 * l)
            - 1.25 * e * e * Math.Sin(2 * m));
        double hourAngle = (utc.TimeOfDay.TotalMinutes / 4 + equationMinutes / 4
                            + site.LongitudeDegrees - 180) * rad;
        var (sp, cp) = Math.SinCos(site.LatitudeDegrees * rad);
        var (sd, cd) = Math.SinCos(declination);
        var (sh, ch) = Math.SinCos(hourAngle);
        // Geocentric Sun vector in local east/north/up axes; atan2 remains stable at the poles.
        double east = -cd * sh, north = cp * sd - sp * cd * ch, up = sp * sd + cp * cd * ch;

        // Retain the existing elevation contract: subtract a WGS84 observer vector in AU.
        // This small topocentric correction is additional to NOAA's geocentric calculator.
        double distanceAu = 1.000001018 * (1 - e * e) / (1 + e * Math.Cos(m + c * rad));
        const double eccentricitySquared = 0.0066943799901413165;
        double n = 6378137 / Math.Sqrt(1 - eccentricitySquared * sp * sp);
        double observerNorth = -n * eccentricitySquared * sp * cp / 149597870700;
        double observerUp = (n * (1 - eccentricitySquared * sp * sp) + site.ElevationMetres) / 149597870700;
        north -= observerNorth / distanceAu;
        up -= observerUp / distanceAu;
        double horizontal = Math.Sqrt(east * east + north * north);
        // Azimuth is undefined at exact zenith/nadir: choose zero deterministically.
        double azimuth = horizontal < 1e-15 ? 0 : Normalize(Math.Atan2(east, north) / rad);
        return new(azimuth, Math.Atan2(horizontal, up) / rad);
    }

    private static double Normalize(double degrees) => (degrees % 360 + 360) % 360;

    public IReadOnlyList<SolarPositionRow> Calculate(IReadOnlyList<DateTimeOffset> timestamps)
        => timestamps.Select(t => new SolarPositionRow(t, Calculate(t))).ToArray();

    /// <summary>Complete timestamp/zenith/azimuth export, including night labels, for later panel-incidence work.</summary>
    public IReadOnlyList<SolarPositionRow> ComputeToWorkbook(IReadOnlyList<DateTimeOffset> timestamps, string outputXlsx,
        CancellationToken cancellationToken = default)
    {
        var positions = Calculate(timestamps);
        ShadingWorkbook.WriteSolarPositions(outputXlsx, positions, site, cancellationToken);
        return positions;
    }

    /// <summary>Preserves labels/order/offsets. Exactly-zero direct rows incur no solar calculations.
    /// Sub-hour directions use midpoint quadrature; no invented irradiance values are returned.</summary>
    public SolarSequence Prepare(IReadOnlyList<IrradianceSample> samples, TimeSampling sampling,
        int maxDegreeOfParallelism = 0, CancellationToken cancellationToken = default)
    {
        sampling.Validate();
        if (maxDegreeOfParallelism < 0) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        var rows = new SolarRow[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!double.IsFinite(s.DirectHorizontal) || s.DirectHorizontal < 0 ||
                !double.IsFinite(s.DiffuseHorizontal) || s.DiffuseHorizontal < 0)
                throw new ArgumentException("Irradiance must be finite and nonnegative.");
            if (i > 0 && s.TimestampUtc <= samples[i-1].TimestampUtc)
                throw new ArgumentException("Timestamps must be strictly increasing UTC instants; gaps are preserved.");
        }
        var parallel = new ParallelOptions { CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism == 0 ? Math.Max(1, Environment.ProcessorCount / 2) : maxDegreeOfParallelism };
        Parallel.For(0, samples.Count, parallel, i =>
        {
            var s = samples[i];
            if (s.DirectHorizontal == 0) { rows[i] = new(s.TimestampUtc, 0, null, Array.Empty<Direction>()); return; }
            var center = Calculate(s.TimestampUtc);
            var rays = new Direction[sampling.Samples];
            for (int j = 0; j < rays.Length; j++)
            {
                var t = s.TimestampUtc.AddMinutes(sampling.StartOffsetMinutes + sampling.DurationMinutes*(j+0.5)/rays.Length);
                rays[j] = t == s.TimestampUtc ? center.Direction : Calculate(t).Direction;
            }
            rows[i] = new(s.TimestampUtc, s.DirectHorizontal, center, Array.AsReadOnly(rays));
        });
        return new(site, sampling, Array.AsReadOnly(rows));
    }
}
