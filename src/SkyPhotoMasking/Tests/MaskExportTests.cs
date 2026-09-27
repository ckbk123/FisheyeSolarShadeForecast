using OpenCvSharp;
using SolarShade.SkyPhotoMasking;
using Xunit;

public sealed class MaskExportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mask-export-tests-" + Guid.NewGuid().ToString("N"));

    private static SkyMaskResult Result(byte level = 255)
    {
        using var mask = new Mat(16, 20, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(mask, new Rect(5, 4, 10, 8), new Scalar(level), -1);
        Cv2.ImEncode(".png", mask, out var png);
        return new(png, SkyModel.EfficientNetB4, 20, 16, 512, new(10, 8, 7, 20, 16), new(3, 1, 15, 15),
            "Fixture", "CPU", null, 0, 0, 0, null, null);
    }

    [Fact]
    public void NativeMaskExportAndCacheLoadPreserveEveryReturnedByte()
    {
        var result = Result();
        var paths = SkyMaskExporter.Export(result, directory);
        Assert.Equal(result.Png, File.ReadAllBytes(paths[0]));
        var loaded = SkyMaskExporter.Load(directory);
        Assert.Equal(result.Png, loaded.Png); Assert.Equal(result.Disk, loaded.Disk);
        Assert.Equal(result.Model, loaded.Model); Assert.Equal(result.Crop, loaded.Crop);
        var reused = SkyMaskExporter.Export(loaded, Path.Combine(directory, "reused"), "Reused");
        Assert.Equal(result.Png, File.ReadAllBytes(reused[0]));
        Assert.Contains("Reused", File.ReadAllText(reused[1]));
    }

    [Fact]
    public void NonbinaryAndWrongDimensionResultsAreNotPublished()
    {
        Assert.Throws<InvalidDataException>(() => SkyMaskExporter.Export(Result(127), directory));
        Assert.Throws<InvalidDataException>(() => SkyMaskExporter.Export(Result() with { Width = 21 }, directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void CancelledExportDoesNotPublishAndWriteFailureIsVisible()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => SkyMaskExporter.Export(Result(), directory, cancellationToken: cts.Token));
        Directory.CreateDirectory(directory); string blocked = Path.Combine(directory, "file"); File.WriteAllText(blocked, "occupied");
        Assert.Throws<IOException>(() => SkyMaskExporter.Export(Result(), blocked));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
