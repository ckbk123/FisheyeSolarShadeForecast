using OpenCvSharp;
using SolarShade.Calibration.Solver;
using SolarShade.Core.Models;
using System.Security.Cryptography;
using Xunit;

namespace SolarShade.Calibration.Tests;

[Collection("OmniCalib pipeline")]
public sealed class ImagePreparationTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void SingleRead_PreservesAllExifOrientationsAndSourceHash(int orientation)
    {
        using var directory = new TemporaryDirectory();
        using var board = Board();
        Assert.True(Cv2.ImEncode(".jpg", board, out var jpeg));
        // Minimal EXIF APP1 segment: little-endian TIFF with one SHORT orientation tag.
        byte[] app1 = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0,
            0x49, 0x49, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01,
            3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        byte[] encoded = [.. jpeg.Take(2), .. app1, .. jpeg.Skip(2)];
        File.WriteAllBytes(Path.Combine(directory.Path, "board.jpg"), encoded);
        var settings = CheckerboardDetectionSettings.CreateVerificationDefault() with { DownsampleFactor = 2 };
        var port = new OmniCalibCSharpPort();
        var reference = port.Detect(directory.Path, settings);
        var singleRead = port.Detect(directory.Path, settings with { ReadImageOnce = true });
        Assert.Equal(orientation >= 5 ? new[] { board.Height, board.Width } : new[] { board.Width, board.Height },
            singleRead.ImageSizeWidthHeight);
        Assert.Equal(reference.ImageSizeWidthHeight, singleRead.ImageSizeWidthHeight);
        var original = Assert.Single(reference.Images);
        var actual = Assert.Single(singleRead.Images);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant(), actual.Sha256);
        Assert.Equal(original.Sha256, actual.Sha256);
        Assert.Equal(54, actual.PointsXy.Length);
        for (var point = 0; point < 54; point++) Assert.Equal(original.PointsXy[point], actual.PointsXy[point]);
    }

    [Fact]
    public void FastSearch_RetainsFallbacksAndReportsRejectedImages()
    {
        using var directory = new TemporaryDirectory();
        using var board = Board();
        using var blank = new Mat(board.Size(), MatType.CV_8UC1, Scalar.White);
        Assert.True(Cv2.ImWrite(Path.Combine(directory.Path, "board.png"), board));
        Assert.True(Cv2.ImWrite(Path.Combine(directory.Path, "blank.png"), blank));
        File.WriteAllBytes(Path.Combine(directory.Path, "empty.png"), []);
        var result = new OmniCalibCSharpPort().ProfileDetection(directory.Path,
            CheckerboardDetectionSettings.CreateFastDefault() with { DownsampleFactor = 2 });
        Assert.Single(result.Observations.Images);
        Assert.Equal(3, result.Images.Count);
        var rejected = Assert.Single(result.Images, i => Path.GetFileName(i.Path) == "blank.png");
        Assert.False(rejected.Found);
        Assert.Equal(2, rejected.SectorAttempts);
        Assert.Equal(1, rejected.ExhaustiveAttempts);
        Assert.Equal(1, rejected.ClassicAttempts);
        Assert.False(Assert.Single(result.Images, i => Path.GetFileName(i.Path) == "empty.png").Found);
        Assert.Contains(nameof(CheckerboardSearchMode.SectorFastFirst), result.Observations.Detector.Sequence);
    }

    private static Mat Board()
    {
        const int square = 32, margin = 40;
        var board = new Mat(10 * square + 2 * margin, 7 * square + 2 * margin, MatType.CV_8UC1, Scalar.White);
        for (var row = 0; row < 10; row++)
            for (var column = 0; column < 7; column++)
                if ((row + column) % 2 == 0)
                    Cv2.Rectangle(board, new Rect(margin + column * square, margin + row * square, square, square),
                        Scalar.Black, -1);
        return board;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmniCalibTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
