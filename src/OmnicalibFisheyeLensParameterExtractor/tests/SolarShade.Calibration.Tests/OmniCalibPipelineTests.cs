using System.Globalization;
using OpenCvSharp;
using SolarShade.Calibration.Detection;
using SolarShade.Calibration.Solver;
using SolarShade.Core.Models;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace SolarShade.Calibration.Tests;

// Prevent this integration fixture from overlapping other native image operations.
// Thread settings are configured only through the production detector's shared lock.
[CollectionDefinition("OmniCalib pipeline", DisableParallelization = true)]
public sealed class OmniCalibPipelineCollection;

[Collection("OmniCalib pipeline")]
public sealed class OmniCalibPipelineTests
{
    [Fact]
    public void WriteYaml_RoundTripsPolynomialOrderAndPoseShapeUnderCommaDecimalCulture()
    {
        using var directory = new TemporaryDirectory();
        var solution = new CalibrationReferenceSolution(1, "test", [640, 480], 22, [6, 9],
            [320.25, 240.75], [0, 500.125, -2.75, 0.03125], [810.5, 0, -0.00025],
            [ [[1, 0, 0, 1.25], [0, 1, 0, -2.5], [0, 0, 1, 400.125]],
              [[1, 0, 0, -7.75], [0, 1, 0, 8.5], [0, 0, 1, 600.25]] ]);
        var path = Path.Combine(directory.Path, "nested", "calibration.yml");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            OmniCalibCSharpPort.WriteYaml(path, solution);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }

        var yaml = new YamlStream();
        using var reader = File.OpenText(path);
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        Assert.Equal(solution.PrincipalPointXy, Values(root["principal_point"]));
        Assert.Equal(solution.IncidentAngleToRadiusPolynomial, Values(root["poly_incident_angle_to_radius"]));
        Assert.Equal(solution.RadiusToZPolynomial, Values(root["poly_radius_to_z"]));
        Assert.Equal(new double[] { 640, 480 }, Values(root["image_size"]));
        var poses = Assert.IsType<YamlSequenceNode>(root["extrinsics"]);
        Assert.Equal(2, poses.Children.Count);
        for (var image = 0; image < poses.Children.Count; image++)
        {
            var rows = Assert.IsType<YamlSequenceNode>(poses.Children[image]);
            Assert.Equal(3, rows.Children.Count);
            for (var row = 0; row < 3; row++)
            {
                var values = Values(rows.Children[row]);
                Assert.Equal(4, values.Length);
                Assert.Equal(solution.Extrinsics[image][row], values);
            }
        }
    }

    [Fact]
    public void Detect_SyntheticBoardsAgreeAcrossWorkerCountsAndCompatibilityEntryPoint()
    {
        using var directory = new TemporaryDirectory();
        const int columns = 6, rows = 9, square = 40, margin = 60;
        const int width = (columns + 1) * square + 2 * margin;
        const int height = (rows + 1) * square + 2 * margin;
        using (var board = new Mat(height, width, MatType.CV_8UC1, Scalar.White))
        {
            for (var row = 0; row <= rows; row++)
                for (var column = 0; column <= columns; column++)
                    if ((row + column) % 2 == 0)
                        Cv2.Rectangle(board, new Rect(margin + column * square, margin + row * square,
                            square, square), Scalar.Black, -1);
            // Names deliberately exercise stable natural ordering, not completion order.
            foreach (var name in new[] { "10.png", "2.png", "1.png" })
                Assert.True(Cv2.ImWrite(Path.Combine(directory.Path, name), board));
        }
        var settings = new CheckerboardDetectionSettings(columns, rows, 22, DownsampleFactor: 2);
        var pipeline = new OmniCalibCSharpPort();
        var serial = pipeline.Detect(directory.Path, settings);
        var parallel = pipeline.Detect(directory.Path, settings with { ImageWorkerCount = 3 });
        var compatible = new CheckerboardObservationExtractor().Extract(directory.Path, settings);

        Assert.Equal(new[] { "1.png", "2.png", "10.png" }, serial.Images.Select(image => image.Name));
        foreach (var result in new[] { serial, parallel, compatible })
        {
            Assert.Equal(new[] { width, height }, result.ImageSizeWidthHeight);
            Assert.Equal("xy_zero_based_after_exif_orientation", result.CoordinateOrder);
            Assert.Equal(3, result.Images.Count);
            Assert.Equal(columns * rows, result.ObjectPointsXyz.Length);
            for (var image = 0; image < result.Images.Count; image++)
            {
                Assert.Equal(columns * rows, result.Images[image].PointsXy.Length);
                Assert.Equal(serial.Images[image].Name, result.Images[image].Name);
                for (var point = 0; point < columns * rows; point++)
                    Assert.Equal(serial.Images[image].PointsXy[point], result.Images[image].PointsXy[point]);
                // Independently verify detections lie on the generated grid, regardless of board orientation.
                foreach (var point in result.Images[image].PointsXy)
                {
                    var nearestX = Enumerable.Range(1, columns).Min(i => Math.Abs(point[0] - (margin + i * square - 0.5)));
                    var nearestY = Enumerable.Range(1, rows).Min(i => Math.Abs(point[1] - (margin + i * square - 0.5)));
                    Assert.InRange(nearestX, 0, 0.1);
                    Assert.InRange(nearestY, 0, 0.1);
                }
            }
        }
    }

    [Fact]
    public void Detect_RejectsInvalidBoardAndWorkerSettingsBeforeReadingImages()
    {
        using var directory = new TemporaryDirectory();
        var valid = new CheckerboardDetectionSettings();
        foreach (var invalid in new[]
        {
            valid with { InnerColumns = 1 }, valid with { InnerRows = 1 },
            valid with { DownsampleFactor = 0 }, valid with { ImageWorkerCount = 0 },
            valid with { OpenCvThreadCount = 0 }, valid with { SquareSizeMillimetres = 0 },
            valid with { SquareSizeMillimetres = double.NaN }, valid with { SquareSizeMillimetres = double.PositiveInfinity },
            valid with { SubpixelEpsilon = -1 }, valid with { SubpixelEpsilon = double.NaN },
            valid with { SubpixelEpsilon = double.PositiveInfinity }
        })
            Assert.Throws<ArgumentOutOfRangeException>(() => new OmniCalibCSharpPort().Detect(directory.Path, invalid));
    }

    private static double[] Values(YamlNode node) => Assert.IsType<YamlSequenceNode>(node).Children
        .Select(value => double.Parse(Assert.IsType<YamlScalarNode>(value).Value!, CultureInfo.InvariantCulture)).ToArray();

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmniCalibTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
