using OpenCvSharp;
using SolarShade.Core.Models;
using YamlDotNet.Serialization;

namespace SolarShade.Camera;

public sealed class OmniCalibProfileImporter
{
    public CalibrationResult ImportLegacyYaml(string yamlPath, string referenceImagePath)
    {
        if (!File.Exists(yamlPath)) throw new FileNotFoundException("OmniCalib profile not found.", yamlPath);
        using var image = Cv2.ImRead(referenceImagePath, ImreadModes.Grayscale);
        if (image.Empty()) throw new InvalidDataException("The reference image could not be opened.");

        var deserializer = new DeserializerBuilder().Build();
        using var reader = File.OpenText(yamlPath);
        var root = deserializer.Deserialize<Dictionary<object, object>>(reader)
                   ?? throw new InvalidDataException("The OmniCalib YAML file is empty.");
        var polynomial = ReadNumberList(root, "poly_incident_angle_to_radius");
        var principalPoint = ReadNumberList(root, "principal_point");
        if (polynomial.Length < 2)
            throw new InvalidDataException("The profile has no usable poly_incident_angle_to_radius coefficients.");
        if (principalPoint.Length != 2)
            throw new InvalidDataException("The profile principal_point must contain exactly two values.");

        var edgeRadius = Math.Min(
            Math.Min(principalPoint[0], image.Width - 1 - principalPoint[0]),
            Math.Min(principalPoint[1], image.Height - 1 - principalPoint[1]));
        var fov = TryReadScalar(root, "fov")
                  ?? throw new InvalidDataException(
                      "The profile must contain an explicit validated 'fov' half-angle; it cannot be inferred safely from an unconstrained polynomial.");
        if (fov is <= 0 or > 180)
            throw new InvalidDataException($"The imported maximum incident angle ({fov:F2}°) is outside the supported range.");
        var imageRadius = Math.Min(edgeRadius, EvaluatePolynomial(polynomial, fov * Math.PI / 180.0));

        return new CalibrationResult(
            SchemaVersion: 2,
            CameraModel: CameraModelKind.OmniCalibIncidentAnglePolynomial,
            ImageWidth: image.Width,
            ImageHeight: image.Height,
            PrincipalPoint: principalPoint,
            IncidentAngleToRadiusPolynomial: polynomial,
            MaximumIncidentAngleDegrees: fov,
            ImageCircleRadiusPixels: imageRadius,
            RmsError: null,
            UsedImageCount: 0,
            RejectedImages: Array.Empty<string>(),
            CreatedUtc: File.GetLastWriteTimeUtc(yamlPath),
            Algorithm: "py-omnicalib fourth-degree incident-angle polynomial (legacy import)");
    }

    private static double[] ReadNumberList(Dictionary<object, object> root, string key)
    {
        if (!TryGet(root, key, out var raw) || raw is not System.Collections.IEnumerable values || raw is string)
            throw new InvalidDataException($"Required OmniCalib field '{key}' is missing or malformed.");
        return values.Cast<object>().Select(ToDouble).ToArray();
    }

    private static double? TryReadScalar(Dictionary<object, object> root, string key)
    {
        if (!TryGet(root, key, out var raw)) return null;
        if (raw is System.Collections.IEnumerable values && raw is not string)
        {
            var first = values.Cast<object>().FirstOrDefault();
            return first is null ? null : ToDouble(first);
        }
        return ToDouble(raw);
    }

    private static bool TryGet(Dictionary<object, object> root, string key, out object value)
    {
        foreach (var pair in root)
        {
            if (string.Equals(Convert.ToString(pair.Key), key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }
        value = null!;
        return false;
    }

    private static double ToDouble(object value) => value switch
    {
        double number => number,
        float number => number,
        int number => number,
        long number => number,
        decimal number => (double)number,
        _ when double.TryParse(Convert.ToString(value), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new InvalidDataException($"'{value}' is not a valid numeric OmniCalib coefficient.")
    };

    internal static double EvaluatePolynomial(IReadOnlyList<double> coefficients, double thetaRadians)
    {
        var result = 0.0;
        for (var i = coefficients.Count - 1; i >= 0; i--) result = result * thetaRadians + coefficients[i];
        return result;
    }
}
