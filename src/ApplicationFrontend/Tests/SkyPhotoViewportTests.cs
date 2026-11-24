using System.IO;
using System.Windows;
using System.Windows.Automation;
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
    public void ARefinedEdgeStopsAtThePhotoPixelsInThePreviewAndEditor() => ManualUpdateTests.Sta(() =>
    {
        var photo = Bitmap(100, 200);
        var viewport = new SkyPhotoViewport { Width = 300, Height = 200 };
        viewport.SetPhoto(photo, 100, 200, "");
        viewport.SetEdge(49.5, 99.5, 70);
        viewport.Measure(new Size(300, 200)); viewport.Arrange(new Rect(0, 0, 300, 200));
        VerifyClip(viewport, () => viewport.ImageBounds);
        viewport.ZoomAt(new Point(150, 70), 1.5); viewport.PanBy(new Vector(10, -10));
        VerifyClip(viewport, () => viewport.ImageBounds);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(photo));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] png = stream.ToArray();
        var editor = new MaskEditorWindow(new(png, png, 49.5, 99.5, 50, "Test mask", (_, _) => true)
        {
            EdgeOutline = viewport.EdgeOutline, FieldOfViewLabel = "Edge"
        }) { Width = 780, Height = 500, ShowActivated = false };
        try
        {
            editor.Show(); editor.UpdateLayout();
            var root = (StackPanel)((ScrollViewer)editor.Content).Content;
            var scroll = Assert.Single(root.Children.OfType<ScrollViewer>());
            var plane = (Grid)scroll.Content;
            var zoom = Assert.Single(root.Children.OfType<WrapPanel>()).Children.OfType<Slider>().Single(s => s.Maximum == 800);
            Rect PhotoBounds() => plane.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, 100, 200));
            zoom.Value = 100;
            VerifyClip(scroll, PhotoBounds);
            zoom.Value = 220;
            scroll.ScrollToVerticalOffset(72);
            VerifyClip(scroll, PhotoBounds);
        }
        finally { editor.Close(); }
        return Task.CompletedTask;

        static void VerifyClip(FrameworkElement surface, Func<Rect> photoBounds)
        {
            surface.UpdateLayout();
            Rect bounds = photoBounds();
            int width = (int)surface.ActualWidth, height = (int)surface.ActualHeight;
            Assert.True(bounds.Left > 1 || bounds.Right < width - 1); // Visible letterbox beside the native photo.
            var render = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            render.Render(surface);
            byte[] pixels = new byte[width * height * 4];
            render.CopyPixels(pixels, width * 4, 0);
            int green = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                if (pixels[offset + 1] < 120 || pixels[offset + 1] < pixels[offset + 2] + 40 ||
                    pixels[offset + 1] < pixels[offset] + 25) continue;
                green++;
                Assert.True(bounds.Contains(new Point(x + .5, y + .5)), $"Edge pixel ({x}, {y}) outside photo {bounds}");
            }
            Assert.True(green > 15);
        }
    });

    [Fact]
    public void FailedCalibrationStampStaysOnTheVisiblePhotoAndClearsForAValidProfile() => ManualUpdateTests.Sta(() =>
    {
        using var fixture = new ManualUpdateTests.Fixture();
        var viewport = new SkyPhotoViewport { Width = 240, Height = 120 };
        viewport.SetPhoto(Bitmap(201, 303), 201, 303, "");
        viewport.Photo.SetOverlay(Bitmap(201, 303));
        viewport.SetFieldOfView(SkyPhotoFieldOfView.CalibrationFailed("Too few complete boards."));
        viewport.Measure(new Size(240, 120)); viewport.Arrange(new Rect(0, 0, 240, 120)); viewport.UpdateLayout();
        var stamp = Assert.Single(viewport.Children.OfType<CalibrationStamp>());
        Assert.Equal(Visibility.Visible, stamp.Visibility);
        Assert.Equal("CALIBRATION FAILED", AutomationProperties.GetName(stamp));
        Assert.Contains("Too few complete boards.", AutomationProperties.GetHelpText(viewport));
        Assert.False(stamp.IsHitTestVisible);
        Assert.Null(viewport.EdgeOutline);
        Assert.False(viewport.Photo.OverlayVisible);
        void StampCoversVisiblePhoto()
        {
            viewport.UpdateLayout();
            var bounds = stamp.TransformToAncestor(viewport).TransformBounds(new Rect(stamp.RenderSize));
            var photoArea = Rect.Intersect(viewport.ImageBounds, new Rect(viewport.RenderSize));
            Assert.Equal(photoArea.X, bounds.X, 7);
            Assert.Equal(photoArea.Y, bounds.Y, 7);
            Assert.Equal(photoArea.Width, bounds.Width, 7);
            Assert.Equal(photoArea.Height, bounds.Height, 7);
        }
        StampCoversVisiblePhoto();
        viewport.ZoomAt(new Point(100, 50), 4);
        viewport.PanBy(new Vector(1000, 1000));
        StampCoversVisiblePhoto();
        var mismatched = SkyPhotoFieldOfView.Load(fixture.Settings, 303, 201, null);
        viewport.SetFieldOfView(mismatched);
        Assert.Equal("PROFILE UNUSABLE", AutomationProperties.GetName(stamp));
        Assert.Contains("dimensions", AutomationProperties.GetHelpText(stamp));
        viewport.SetFieldOfView(SkyPhotoFieldOfView.Load(fixture.Settings, 201, 303, null));
        Assert.Equal(Visibility.Collapsed, stamp.Visibility);
        Assert.NotNull(viewport.EdgeOutline);
        viewport.SetFieldOfView(mismatched);
        viewport.ClearPhoto();
        Assert.Equal(Visibility.Collapsed, stamp.Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public void WarningStampScalesWithThePreviewWithinItsScreenSizeLimit() => ManualUpdateTests.Sta(() =>
    {
        var stamp = new CalibrationStamp();
        stamp.SetMessage(CalibrationStamp.FailureLabel);
        var badge = Assert.Single(stamp.Children.OfType<Viewbox>());
        double RenderedWidth(double width, double height)
        {
            stamp.Measure(new Size(width, height));
            stamp.Arrange(new Rect(0, 0, width, height));
            stamp.UpdateLayout();
            Assert.InRange(badge.ActualWidth, 1, Math.Min(410, width * .8) + 1);
            Assert.InRange(badge.ActualHeight, 1, height * .44 + 1);
            return badge.ActualWidth;
        }
        double compact = RenderedWidth(160, 220);
        double expanded = RenderedWidth(740, 550);
        Assert.True(expanded >= compact * 2);
        Assert.Equal(410, expanded, 7);
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
