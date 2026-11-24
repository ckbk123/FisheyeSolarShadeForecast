using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class MaskPreviewZoomTests
{
    [Fact]
    public void MaskControlsInspectTheNativeRasterAndSunPathIndependentlyOfThePhoto() => Sta(async () =>
    {
        using var fixture = new Fixture();
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.Show();
            window.Calculate();
            await Until(() => !window.UpdatingForTest);
            var study = Assert.IsType<Evaluation>(window.Completed);
            var mask = Descendants<MaskViewport>(window).Single();
            var sky = Descendants<SkyPhotoViewport>(window).Single();
            var pane = Assert.IsType<Grid>(mask.Parent);
            var edit = pane.Children.OfType<Button>().Single(b => Equals(b.Content, "Edit mask"));
            var layers = ((Grid)mask.Layers.Child).Children.OfType<Image>().ToArray();
            var binary = Assert.IsAssignableFrom<BitmapSource>(layers[0].Source);
            var path = Assert.IsAssignableFrom<BitmapSource>(layers[1].Source);
            Assert.Equal(study.Mask!.Result.Width, binary.PixelWidth);
            Assert.Equal(study.Mask.Result.Height, binary.PixelHeight);
            Assert.Equal(binary.PixelWidth, path.PixelWidth);
            double originalWidth = mask.ActualWidth;

            Button Tool(string id) => Descendants<Button>(pane).Single(b => AutomationProperties.GetAutomationId(b) == id);
            void Click(string id)
            {
                var button = Tool(id);
                Assert.True(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
            }
            void Press(Key key) => mask.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(mask), 0, key)
                { RoutedEvent = Keyboard.KeyDownEvent });

            Click("ExpandSkyMask");
            Assert.False(sky.IsVisible);
            Assert.True(mask.ActualWidth > originalWidth * 1.8);
            Assert.Equal(0, Grid.GetColumn(pane));
            Assert.True(edit.IsEnabled);
            var editBounds = edit.TransformToAncestor(pane).TransformBounds(new Rect(edit.RenderSize));
            Assert.InRange(editBounds.Top, 0, 10);
            Assert.InRange(pane.ActualWidth - editBounds.Right, 0, 10);

            Click("ZoomSkyMaskIn");
            Press(Key.Add);
            Assert.Equal(2.25, mask.Zoom);
            Press(Key.Down);
            Assert.Equal(1, sky.Zoom);
            Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(layers[0]));
            var sample = new Point(50, 86);
            Assert.Equal(layers[0].TransformToAncestor(mask).Transform(sample), layers[1].TransformToAncestor(mask).Transform(sample));
            window.SetSunPathVisibleForTest(false);
            Assert.False(mask.Layers.OverlayVisible);
            window.SetSunPathVisibleForTest(true);
            Assert.True(mask.Layers.OverlayVisible);
            Assert.Same(binary, layers[0].Source);
            Assert.Same(path, layers[1].Source);
            Assert.Same(study, window.Completed);
            Assert.Equal(1, fixture.Runs);

            Click("ZoomSkyMaskOut");
            Press(Key.Home);
            Assert.Equal(1, mask.Zoom);
            Click("ExpandSkyMask");
            Assert.True(sky.IsVisible);
            Assert.Equal(2, Grid.GetColumn(pane));
            Assert.Equal(originalWidth, mask.ActualWidth, 5);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void SwitchingFromImportedToMissingMaskClearsZoomAndAllowsRestoringBothPanes() => Sta(async () =>
    {
        using var fixture = new Fixture();
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.Show();
            var imported = Path.Combine(fixture.Root, "imported.png");
            using (var pixels = new OpenCvSharp.Mat(303, 201, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.White))
                OpenCvSharp.Cv2.ImWrite(imported, pixels);
            window.Irradiance.ImportMaskForTest(imported, "Imported mask");
            await window.InputsSettled;
            var viewport = Descendants<MaskViewport>(window).Single();
            var pane = Assert.IsType<Grid>(viewport.Parent);
            var expand = Descendants<Button>(pane).Single(b => AutomationProperties.GetAutomationId(b) == "ExpandSkyMask");
            var zoom = Descendants<Button>(pane).Single(b => AutomationProperties.GetAutomationId(b) == "ZoomSkyMaskIn");
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(viewport.Zoom > 1);

            var otherPhoto = Path.Combine(fixture.Root, "new-photo.png");
            using (var pixels = new OpenCvSharp.Mat(303, 201, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black))
                OpenCvSharp.Cv2.ImWrite(otherPhoto, pixels);
            window.Irradiance.SelectSkyPhotoForTest(otherPhoto);
            await window.InputsSettled;
            Assert.False(viewport.HasImage);
            Assert.Equal(1, viewport.Zoom);
            Assert.False(viewport.Layers.OverlayVisible);
            Assert.False(zoom.IsEnabled);
            Assert.False(pane.Children.OfType<Button>().Single().IsEnabled);
            Assert.True(expand.IsEnabled);
            expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(expand.IsEnabled);
            Assert.True(Descendants<SkyPhotoViewport>(window).Single().IsVisible);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
}
