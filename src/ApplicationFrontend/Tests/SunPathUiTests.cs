using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.Desktop;
using Xunit;

public sealed class SunPathUiTests
{
    private static BitmapSource Image(int width, int height, byte red, byte green, byte blue, byte alpha)
    {
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < bytes.Length; i += 4)
        { bytes[i] = blue; bytes[i + 1] = green; bytes[i + 2] = red; bytes[i + 3] = alpha; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        image.Freeze(); return image;
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory][InlineData(301, 199)][InlineData(211, 407)]
    public void MaskAndOverlayHaveOneNativeCoordinateTransform(int viewportWidth, int viewportHeight) => Sta(() =>
    {
        var preview = new MaskOverlayPreview { Width = viewportWidth, Height = viewportHeight };
        preview.SetMask(Image(37, 53, 255, 255, 255, 255));
        preview.SetOverlay(Image(37, 53, 255, 80, 0, 180));
        preview.Measure(new Size(viewportWidth, viewportHeight));
        preview.Arrange(new Rect(0, 0, viewportWidth, viewportHeight)); preview.UpdateLayout();
        var surface = Assert.IsType<Grid>(preview.Child);
        Assert.Equal(37, surface.Width); Assert.Equal(53, surface.Height);
        var images = surface.Children.Cast<System.Windows.Controls.Image>().ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal(images[0].RenderSize, images[1].RenderSize);
        foreach (var point in new[] { new Point(0, 0), new Point(18, 26), new Point(37, 53) })
            Assert.Equal(images[0].TransformToAncestor(preview).Transform(point), images[1].TransformToAncestor(preview).Transform(point));
        Assert.True(preview.OverlayVisible);
    });

    [Fact] public void ReducedColorPhotoAndNativeOverlayUseSameCanvasWithoutChangingPhoto() => Sta(() =>
    {
        var preview = new ImageOverlayPreview { Width = 211, Height = 407 };
        var color = Image(333, 500, 20, 140, 220, 255);
        preview.SetImage(color, 2003, 3001);
        preview.SetOverlay(Image(2003, 3001, 255, 40, 160, 180));
        preview.Measure(new Size(211, 407)); preview.Arrange(new Rect(0, 0, 211, 407)); preview.UpdateLayout();
        var surface = Assert.IsType<Grid>(preview.Child);
        Assert.Equal(2003, surface.Width); Assert.Equal(3001, surface.Height);
        var layers = surface.Children.Cast<System.Windows.Controls.Image>().ToArray();
        Assert.Same(color, layers[0].Source); Assert.Equal(layers[0].RenderSize, layers[1].RenderSize);
        foreach (var point in new[] { new Point(0, 0), new Point(1001, 1500), new Point(2003, 3001) })
            Assert.Equal(layers[0].TransformToAncestor(preview).Transform(point), layers[1].TransformToAncestor(preview).Transform(point));
        preview.ShowOverlay = false; Assert.Same(color, layers[0].Source);
        Assert.Throws<ArgumentException>(() => preview.SetOverlay(Image(333, 500, 255, 0, 0, 180)));
    });

    [Fact] public void ToggleKeepsMaskAndOverlaySourcesAndNewMaskClearsOldOverlay() => Sta(() =>
    {
        var preview = new MaskOverlayPreview();
        var mask = Image(37, 53, 255, 255, 255, 255);
        var path = Image(37, 53, 255, 80, 0, 180);
        preview.SetMask(mask); preview.SetOverlay(path);
        var surface = (Grid)preview.Child;
        preview.ShowOverlay = false; Assert.False(preview.OverlayVisible);
        Assert.Same(mask, ((System.Windows.Controls.Image)surface.Children[0]).Source);
        Assert.Same(path, ((System.Windows.Controls.Image)surface.Children[1]).Source);
        preview.ShowOverlay = true; Assert.True(preview.OverlayVisible);
        preview.SetMask(mask); Assert.True(preview.OverlayVisible);
        preview.SetMask(Image(37, 53, 255, 255, 255, 255)); Assert.False(preview.OverlayVisible);
        Assert.Null(((System.Windows.Controls.Image)surface.Children[1]).Source);
        Assert.Throws<ArgumentException>(() => preview.SetOverlay(Image(36, 53, 255, 80, 0, 180)));
    });
}
