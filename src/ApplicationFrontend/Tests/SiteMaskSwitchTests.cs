using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using SolarShade.Desktop;
using SolarShade.MaskEditor;
using SolarShade.Shading;
using SolarShade.ShadingCorrection;
using SolarShade.SkyPhotoMasking;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class SiteMaskSwitchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChoosingAnotherSitesPhotoWithTheSameNameReplacesTheEditedMaskInPreviewEditorAndDebug(bool interruptShading) => Sta(async () =>
    {
        using var fixture = new Fixture();
        var first = fixture.Settings;
        var template = AppServices.LoadCachedAiMask(first)!.Result;
        string secondSite = Directory.CreateDirectory(Path.Combine(fixture.Root, "site-b")).FullName;
        string secondPhoto = Path.Combine(secondSite, Path.GetFileName(first.SkyImage));
        using var secondImage = new Mat(template.Height, template.Width, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(secondImage, new OpenCvSharp.Rect(65, 60, 90, 150), Scalar.White, -1);
        Cv2.ImWrite(secondPhoto, secondImage);
        using var secondMask = new Mat();
        Cv2.CvtColor(secondImage, secondMask, ColorConversionCodes.BGR2GRAY);
        Cv2.ImEncode(".png", secondMask, out var expectedPng);
        var expectedPixels = SkyShadingModule.DecodeMask(expectedPng).Pixels;
        string blocked = Path.Combine(fixture.Root, "blocked.png");
        using (var black = new Mat(template.Height, template.Width, MatType.CV_8UC1, Scalar.Black)) Cv2.ImWrite(blocked, black);

        bool failThisUpdate = false;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        {
            if (failThisUpdate) throw new InvalidOperationException("Site B shading interrupted by the test.");
            return ShadingCorrectionModule.ApplyToPanel(input, scene, ct);
        });
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            window.Show();
            var viewport = Descendants<MaskViewport>(window).Single();
            var editorButton = ((Grid)viewport.Parent).Children.OfType<Button>().Single(b => Equals(b.Content, "Edit mask"));
            var imported = window.Irradiance.ImportMaskForTest(blocked, "Site A");
            using var firstMask = Cv2.ImRead(blocked, ImreadModes.Grayscale);
            Cv2.Circle(firstMask, new OpenCvSharp.Point(45, 145), 8, Scalar.White, -1);
            Cv2.ImEncode(".png", firstMask, out var firstEdit);
            var edited = ManualMaskStore.Save(first.SkyImage, imported.BaseMask, "", imported.Id, "Site A edit", firstEdit);
            // Re-selecting the photo refreshes the available mask variants.
            window.Irradiance.SelectSkyPhotoForTest(first.SkyImage);
            window.Irradiance.SelectMaskForTest(edited.Id);
            await window.InputsSettled;
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.True(window.ExportEnabledForTest);
            string maskPath = Path.Combine(fixture.Debug, "02-sky-mask", "sky-mask.png");
            string manifestPath = Path.Combine(fixture.Debug, "run.json");
            Assert.Equal(edited.PngSha256, AppData.FileKey(maskPath));

            window.Irradiance.SelectSkyPhotoForTest(secondPhoto);
            Assert.Equal(secondPhoto, PortablePaths.Resolve(window.IrradianceController.Settings.SkyImage));
            Assert.Null(window.IrradianceController.Settings.SelectedMaskId);
            Assert.False(viewport.HasImage);
            Assert.False(editorButton.IsEnabled);
            await window.InputsSettled;
            Assert.Equal("Stale", AppData.Read<DebugDataRun.Manifest>(manifestPath)!.Status);
            Assert.False(File.Exists(maskPath));

            // Supply a known AI-stage output for this exact photo so model changes cannot affect this routing test.
            string key = AppData.Key(new { Image = AppData.FileKey(secondPhoto), first.Model, first.Resolution, first.CenteredDisk,
                Library = AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = AppServices.MaskCacheSchema });
            SkyMaskExporter.Export(template with { Png = expectedPng }, AppData.PathFor(Path.Combine("mask-stages", key)), "Site B fixture");
            if (interruptShading)
            {
                failThisUpdate = true;
                window.Calculate(); await Until(() => !window.UpdatingForTest);
                Assert.False(window.ExportEnabledForTest);
                var failed = AppData.Read<DebugDataRun.Manifest>(manifestPath)!;
                Assert.Equal("Failed", failed.Status);
                Assert.Equal(secondPhoto, PortablePaths.Resolve(failed.Settings.SkyImage));
                Assert.Equal("Complete", failed.Stages["02-sky-mask"].Status);
                Assert.Equal(expectedPng, File.ReadAllBytes(maskPath));
                failThisUpdate = false;
            }
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.True(window.ExportEnabledForTest);
            var run = AppData.Read<DebugDataRun.Manifest>(manifestPath)!;
            Assert.Equal("Complete", run.Status);
            Assert.Equal(interruptShading ? 3 : 2, run.UpdateNumber);
            Assert.Equal(secondPhoto, PortablePaths.Resolve(run.Settings.SkyImage));
            Assert.Equal(key, run.Inputs["sky-mask"].GetProperty("Fingerprint").GetString());
            Assert.Equal("Complete", run.Stages["02-sky-mask"].Status);
            Assert.NotEqual(edited.PngSha256, run.ArtifactHashes["02-sky-mask/sky-mask.png"]);
            Assert.Equal(AppData.FileKey(maskPath), run.ArtifactHashes["02-sky-mask/sky-mask.png"]);
            Assert.Equal(expectedPng, File.ReadAllBytes(maskPath));
            Assert.Equal(expectedPng, window.Completed!.Mask!.Png);
            var preview = ((Grid)viewport.Layers.Child).Children.OfType<Image>().First();
            Assert.Equal(expectedPixels, GrayPixels((BitmapSource)preview.Source));

            await Until(() => editorButton.IsEnabled);
            var opening = window.Dispatcher.InvokeAsync(() =>
            {
                var editor = window.OwnedWindows.OfType<MaskEditorWindow>().Single();
                try
                {
                    var openingMask = Descendants<Image>(editor).Select(i => i.Source).OfType<WriteableBitmap>().Single();
                    Assert.Equal(expectedPixels, GrayPixels(openingMask));
                }
                finally { editor.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            editorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await opening.Task;
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    private static byte[] GrayPixels(BitmapSource bitmap)
    {
        var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
        var pixels = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(pixels, gray.PixelWidth, 0);
        return pixels;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
