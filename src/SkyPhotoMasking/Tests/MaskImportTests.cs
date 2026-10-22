using OpenCvSharp;
using SolarShade.SkyPhotoMasking;
using Xunit;

public sealed class MaskImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sky-mask-import-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AutomaticDiskComesFromPhotoAndDoesNotChangeWhenHalfTheMaskIsBlack()
    {
        Directory.CreateDirectory(root);
        string photoPath = Path.Combine(root, "photo.png"), maskPath = Path.Combine(root, "mask.png");
        using var photo = new Mat(600, 800, MatType.CV_8UC3, Scalar.Black);
        Cv2.Circle(photo, new Point(470, 270), 170, new Scalar(180, 190, 210), -1);
        Cv2.ImWrite(photoPath, photo);
        using var input = new Mat(600, 800, MatType.CV_8UC3, Scalar.White);
        Cv2.ImWrite(maskPath, input);
        var white = SkyMaskImporter.Import(photoPath, maskPath);
        Cv2.Rectangle(input, new Rect(0, 0, 470, 600), Scalar.Black, -1);
        Cv2.ImWrite(maskPath, input);
        var half = SkyMaskImporter.Import(photoPath, maskPath);
        Assert.Equal(white.Disk, half.Disk);
        Assert.InRange(half.Disk.CenterX, 466, 474); Assert.InRange(half.Disk.CenterY, 266, 274);
        Assert.InRange(half.Disk.Radius, 166, 174);
        using var decoded = Cv2.ImDecode(half.Png, ImreadModes.Grayscale);
        Assert.Equal((byte)0, decoded.At<byte>(270, 400)); Assert.Equal((byte)255, decoded.At<byte>(270, 540));
        Assert.Equal((byte)0, decoded.At<byte>(0, 0));
        Assert.Equal(0, half.InferenceMilliseconds); Assert.Equal(0, half.SessionLoadMilliseconds);
        Assert.Equal("Imported binary mask · no AI", half.ExecutionProvider);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
