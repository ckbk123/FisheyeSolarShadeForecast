using OpenCvSharp;
using SolarShade.SkyPhotoMasking;
using Xunit;

public sealed class MaskTests
{
    [Fact] public void TensorIsRgbPlanarWithOnly255Normalization()
    {
        using var bgr = new Mat(2, 2, MatType.CV_8UC3, new Scalar(0, 127, 255));
        var tensor = SkyPhotoMasker.ToTensor(bgr);
        Assert.Equal(new[] { 1, 3, 2, 2 }, tensor.Dimensions.ToArray());
        Assert.Equal(1f, tensor[0,0,1,1]); Assert.Equal(127/255f, tensor[0,1,0,1]); Assert.Equal(0, tensor[0,2,1,0]);
    }

    [Theory]
    [InlineData(SkyModel.EfficientNetB4,155)] [InlineData(SkyModel.EfficientNetB5,158)]
    [InlineData(SkyModel.EfficientNetB6,148)] [InlineData(SkyModel.EfficientNetB7,161)]
    public void ThresholdIncludesEqualityAsSkyAndKeepsOutsideBlack(SkyModel model, int level)
    {
        var p = Enumerable.Repeat(level/255f, 25).ToArray();
        p[11] = MathF.BitIncrement(level/255f);
        using var mask = SkyPhotoMasker.Threshold(p, 5, model);
        Assert.Equal(255, mask.At<byte>(2,2)); Assert.Equal(0, mask.At<byte>(2,1));
        Assert.Equal(255, mask.At<byte>(0,2)); Assert.Equal(0, mask.At<byte>(0,0));
    }

    [Theory] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(-1f)]
    public void InvalidProbabilityFailsInsteadOfProducingPlausibleMask(float bad)
    {
        var p = new float[25]; p[12] = bad;
        Assert.Throws<InvalidDataException>(() => SkyPhotoMasker.Threshold(p,5,SkyModel.EfficientNetB4));
    }

    [Fact] public void CropPreservesInclusivePythonBounds()
    {
        Assert.Equal(new MaskCrop(30,40,41,41), SkyPhotoMasker.CropForDisk(new LensDisk(50,60,20,100,120)));
        Assert.Equal(new MaskCrop(0,40,41,41), SkyPhotoMasker.CropForDisk(new LensDisk(10,60,30,100,120)));
    }

    [Fact] public void AutoDetectsOffsetDiskInsteadOfAssumingImageCenter()
    {
        using var source = new Mat(600,800,MatType.CV_8UC3,Scalar.Black);
        Cv2.Circle(source,new Point(470,270),170,new Scalar(180,190,210),-1);
        var disk = SkyPhotoMasker.FindDisk(source,DiskDetection.Auto);
        Assert.InRange(disk.CenterX,466,474); Assert.InRange(disk.CenterY,266,274); Assert.InRange(disk.Radius,166,174);
    }

    [Fact] public void BlankImageDoesNotInventDisk()
    {
        using var source = new Mat(600,800,MatType.CV_8UC3,Scalar.Black);
        Assert.Throws<InvalidDataException>(() => SkyPhotoMasker.FindDisk(source,DiskDetection.Auto));
    }

    [Fact] public void MissingImageAndDisposedInstanceFailClearly()
    {
        using var masker = new SkyPhotoMasker();
        Assert.Throws<FileNotFoundException>(() => masker.CreateMask("missing-"+Guid.NewGuid()+".jpg",SkyModel.EfficientNetB4));
        masker.Dispose();
        Assert.Throws<ObjectDisposedException>(() => masker.CreateMask("anything.jpg",SkyModel.EfficientNetB4));
    }

    [Fact] public void MissingWeightsNeverUsesColorHeuristic()
    {
        var path = Path.Combine(Path.GetTempPath(),$"sky-test-{Guid.NewGuid():N}.png");
        try
        {
            using var image = new Mat(128,128,MatType.CV_8UC3,Scalar.White); Cv2.ImWrite(path,image);
            using var masker = new SkyPhotoMasker(new() { DiskDetection=DiskDetection.Centered, ModelsDirectory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()) });
            Assert.Throws<FileNotFoundException>(() => masker.CreateMask(path,SkyModel.EfficientNetB4));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void CameraProfileMustMatchOrientedImageDimensions()
    {
        var path = Path.Combine(Path.GetTempPath(),$"sky-test-{Guid.NewGuid():N}.png");
        try
        {
            using var image = new Mat(128,256,MatType.CV_8UC3,Scalar.White); Cv2.ImWrite(path,image);
            using var masker = new SkyPhotoMasker(new() { Disk=new LensDisk(128,64,60,128,256) });
            Assert.Throws<InvalidDataException>(() => masker.CreateMask(path,SkyModel.EfficientNetB4));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void UnavailableGpuFallsBackToRealCpuModelAndTwoInputApiReturnsPng()
    {
        var path = Path.Combine(Path.GetTempPath(),$"sky-test-{Guid.NewGuid():N}.png");
        try
        {
            using var image = new Mat(128,128,MatType.CV_8UC3,new Scalar(210,180,140)); Cv2.ImWrite(path,image);
            using var masker = new SkyPhotoMasker(new() { DiskDetection=DiskDetection.Centered,
                InputSize=512, DirectMLDeviceId=int.MaxValue, CpuThreads=2 });
            var detailed = masker.CreateMaskDetailed(path,SkyModel.EfficientNetB4);
            Assert.Equal("CPU",detailed.ExecutionProvider); Assert.NotNull(detailed.AccelerationFallback);
            Assert.Equal(512,detailed.InputSize);
            var png = masker.CreateMask(path,SkyModel.EfficientNetB4);
            Assert.Equal(detailed.Png,png);
            using var mask = Cv2.ImDecode(png,ImreadModes.Grayscale);
            Assert.Equal(128,mask.Width); Assert.Equal(128,mask.Height); Assert.Equal(0,mask.At<byte>(0,0));
            using var invalid = new Mat(); Cv2.InRange(mask,new Scalar(1),new Scalar(254),invalid);
            Assert.Equal(0,Cv2.CountNonZero(invalid));
        }
        finally { File.Delete(path); }
    }
}
