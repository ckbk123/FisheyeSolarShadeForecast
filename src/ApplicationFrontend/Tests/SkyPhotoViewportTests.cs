using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.Desktop;
using SolarShade.MaskEditor;
using Xunit;

[Collection("Manual update UI")]
public sealed class SkyPhotoViewportTests
{
    private static BitmapSource Bitmap(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, new byte[width * height], width);
        image.Freeze();
        return image;
    }

    [Fact]
    public void ZoomPinsThePointerAndPanMovesAllNativeLayersTogether() => ManualUpdateTests.Sta(() =>
    {
        var viewport = new SkyPhotoViewport { Width = 240, Height = 120 };
        viewport.SetPhoto(Bitmap(200, 100), 200, 100, "");
        viewport.Photo.SetOverlay(Bitmap(200, 100));
        viewport.Measure(new Size(240, 120)); viewport.Arrange(new Rect(0, 0, 240, 120)); viewport.UpdateLayout();
        var plane = (Grid)viewport.Children[0];
        var fovGuide = Assert.Single(plane.Children.OfType<FieldOfViewOverlay>());
        var images = ((Grid)viewport.Photo.Child).Children.OfType<Image>().ToArray();
        var nativePoint = new Point(125, 60);
        Point Map(Visual layer) => layer.TransformToAncestor(viewport).Transform(nativePoint);
        void SamePoint(Point expected, Point actual)
        {
            Assert.Equal(expected.X, actual.X, 8);
            Assert.Equal(expected.Y, actual.Y, 8);
        }
        var anchor = Map(images[0]);
        viewport.ZoomAt(anchor, 2);
        SamePoint(anchor, Map(images[0]));
        viewport.PanBy(new(24, 12));
        SamePoint(anchor + new Vector(24, 12), Map(images[0]));
        SamePoint(Map(images[0]), Map(images[1]));
        SamePoint(Map(images[0]), Map(fovGuide));
        viewport.PanBy(new(10000, 10000));
        SamePoint(new(0, 0), viewport.ImageBounds.TopLeft);
        Assert.True(viewport.Photo.OverlayVisible);
        viewport.Fit();
        Assert.Equal(1, viewport.Zoom);
        Assert.Equal(new Rect(0, 0, 240, 120), viewport.ImageBounds);
        SamePoint(anchor, Map(images[0]));
        viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent });
        Assert.True(viewport.Zoom > 1);
        viewport.ClearPhoto();
        Assert.True(viewport.ImageBounds.IsEmpty);
        Assert.False(viewport.Photo.OverlayVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public void GreenCalibrationLineStaysOnTheProjectedPixelsAtReadableWidthWhileZoomed() => ManualUpdateTests.Sta(() =>
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var view = SkyPhotoFieldOfView.Load(fixture.Settings, 201, 303, null);
        var circle = Assert.IsType<EllipseGeometry>(view.CalibratedOutline);
        var viewport = new SkyPhotoViewport { Width = 201, Height = 303 };
        viewport.SetPhoto(Bitmap(201, 303), 201, 303, "");
        viewport.SetFieldOfView(view);
        viewport.Measure(new Size(201, 303)); viewport.Arrange(new Rect(0, 0, 201, 303)); viewport.UpdateLayout();
        var rim = new Point(circle.Center.X + circle.RadiusX, circle.Center.Y);
        int GreenPixelsAcrossRim()
        {
            viewport.UpdateLayout();
            var render = new RenderTargetBitmap(201, 303, 96, 96, PixelFormats.Pbgra32);
            render.Render(viewport);
            var row = new byte[13 * 4];
            render.CopyPixels(new Int32Rect((int)rim.X - 6, (int)rim.Y, 13, 1), row, row.Length, 0);
            return Enumerable.Range(0, 13).Count(i => row[4 * i + 1] > 100 && row[4 * i + 1] > row[4 * i + 2] + 30);
        }
        Assert.InRange(GreenPixelsAcrossRim(), 1, 3);
        viewport.ZoomAt(rim, 4);
        Assert.InRange(GreenPixelsAcrossRim(), 1, 3);
        viewport.SetFieldOfView(null);
        Assert.Equal(0, GreenPixelsAcrossRim());
        return Task.CompletedTask;
    });

    [Fact]
    public void ZoomLoadsOrientedNativePixelsWithoutReplacingTheCardinalsOrAChangedPhoto() => ManualUpdateTests.Sta(async () =>
    {
        using var fixture = new ManualUpdateTests.Fixture();
        // The fixture supplies a real 201 x 303 sky photograph.
        var viewport = new SkyPhotoViewport { Width = 201, Height = 303 };
        viewport.SetPhoto(Bitmap(20, 30), 201, 303, fixture.Settings.SkyImage);
        viewport.Photo.SetOverlay(Bitmap(201, 303));
        viewport.Measure(new Size(201, 303)); viewport.Arrange(new Rect(0, 0, 201, 303)); viewport.UpdateLayout();
        Assert.False(viewport.HasNativePhoto);
        viewport.ZoomAt(new Point(100, 150), 2);
        await ManualUpdateTests.Until(() => viewport.HasNativePhoto);
        var layers = ((Grid)viewport.Photo.Child).Children.OfType<Image>().ToArray();
        var detailed = Assert.IsAssignableFrom<BitmapSource>(layers[0].Source);
        Assert.Equal(201, detailed.PixelWidth); Assert.Equal(303, detailed.PixelHeight);
        Assert.Equal(PixelFormats.Bgr24, detailed.Format);
        Assert.True(viewport.Photo.OverlayVisible);
        viewport.PanBy(new(6, 5)); viewport.ZoomAt(new Point(87, 123), 2);
        Assert.Same(detailed, layers[0].Source);

        viewport.SetPhoto(Bitmap(20, 30), 201, 303, fixture.Settings.SkyImage);
        var pending = viewport.RequestNativePhotoAsync();
        var replacement = Bitmap(10, 20);
        viewport.SetPhoto(replacement, 10, 20, Path.Combine(fixture.Root, "next.png"));
        await pending;
        Assert.Same(replacement, layers[0].Source);
        Assert.False(viewport.Photo.OverlayVisible);
        Assert.Equal(1, viewport.Zoom);
    });
}
