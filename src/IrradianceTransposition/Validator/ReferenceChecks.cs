using System.Text.Json;
using SolarShade.Irradiance.Transposition;

namespace SolarShade.Irradiance.Transposition.Validation;

public static class ReferenceChecks
{
    public sealed record ReferenceSummary(string Pvlib, int Cases, double MaximumAbsoluteErrorWm2,
        int RefractionCases, double MaximumRefractionErrorDegrees);
    public static ReferenceSummary Run(string path)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        int count = 0; double maximum = 0;
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            double Get(string key) => row.GetProperty(key).GetDouble();
            foreach (var (model, key) in new[] { (DiffuseModel.HayDavies, "hay"), (DiffuseModel.PerezDriesse, "perez") })
            {
                var actual = SkyTransposition.EvaluateDni(new(Get("tilt"), Get("azimuth")),
                    new(Get("zenith"), Get("sunAzimuth")), Get("dni"), Get("dhi"), Get("extra"), model);
                double error = Math.Max(Math.Abs(actual.Direct - Get("direct")), Math.Abs(actual.SkyDiffuse - Get(key)));
                maximum = Math.Max(maximum, error);
                if (!double.IsFinite(error) || error > 1e-8)
                    throw new InvalidDataException(FormattableString.Invariant($"pvlib reference mismatch: case {count}, {model}, error {error:R} W/m²."));
            }
            count++;
        }
        if (count < 1000) throw new InvalidDataException("Incomplete reference fixture.");
        int refractionCount = 0; double refractionError = 0;
        foreach (var row in fixture.RootElement.GetProperty("refraction").EnumerateArray())
        {
            var actual = SunPosition.FromGeometricNoaa(row.GetProperty("geometric").GetDouble(), row.GetProperty("azimuth").GetDouble());
            double error = Math.Abs(actual.ZenithDegrees - row.GetProperty("apparent").GetDouble());
            if (!double.IsFinite(error) || error > 1e-10) throw new InvalidDataException("Refraction reference mismatch.");
            refractionError = Math.Max(refractionError, error); refractionCount++;
        }
        if (refractionCount < 100) throw new InvalidDataException("Incomplete refraction fixture.");
        return new(fixture.RootElement.GetProperty("pvlib").GetString()!, count, maximum, refractionCount, refractionError);
    }
}
