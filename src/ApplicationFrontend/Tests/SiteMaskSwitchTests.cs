using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using SolarShade.Camera;
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
    [Fact]
    public void RetryingAnEarlierPhotoChangePreservesTheCurrentSitesReusableShading()
    {
        using var fixture = new Fixture();
        var current = fixture.Settings;
        using var run = new DebugDataRun(current, "calculation", fixture.Debug);
        var inputs = InputDependencies.Capture(current);
        run.RecordInput("sky-mask", new { PhotoSha256 = inputs.Image.Content });
        var values = new CurrentValueCache(fixture.Debug);
        values.Write("shading", "current", "shading for the current photo");
        var previous = new SourceIdentity(current.SkyImage, new string('B', 64));
        run.DiscardPhotoArtifacts(previous, current, inputs);
        Assert.Equal("shading for the current photo", values.Read<string>("shading", "current"));
    }

    [Fact]
    public void ChangingTheSkyPhotoKeepsAnIndependentCheckerboardCalibration()
    {
        using var fixture = new Fixture();
        var first = fixture.Settings;
        string secondPhoto = Path.Combine(fixture.Root, "replacement.png");
        using (var image = new Mat(303, 201, MatType.CV_8UC3, Scalar.Blue)) Cv2.ImWrite(secondPhoto, image);
        var next = first with { SkyImage = secondPhoto };
        using var run = new DebugDataRun(first, "calibration", fixture.Debug);
        string stage = run.BeginStage("01-calibration", "Checkerboard fit", typeof(CameraProfileFiles));
        string published = Path.Combine(stage, "camera-profile.json");
        File.Copy(first.ProfilePath, published);
        run.CompleteStage("01-calibration", [published]); run.Complete();
        var inputs = InputDependencies.Capture(next);
        run.DiscardPhotoArtifacts(InputDependencies.Capture(first).Image, next, inputs);
        Assert.False(run.Differences(inputs).HasFlag(ArtifactGroup.Profile));
        string profile = Path.Combine(fixture.Debug, "01-calibration", "camera-profile.json");
        Assert.Equal(AppData.FileKey(first.ProfilePath), AppData.FileKey(profile));
    }

    [Fact]
    public void SwitchingPhotosWhileShadingRejectsLateResultsAndPublishesOnlyTheNewMask() => Sta(async () =>
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        int shadeCalls = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        {
            if (Interlocked.Increment(ref shadeCalls) == 1)
            {
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Site A shading was not released.");
                // Let native work return a result even after cancellation.
                return ShadingCorrectionModule.ApplyToPanel(input, scene, CancellationToken.None);
            }
            return ShadingCorrectionModule.ApplyToPanel(input, scene, ct);
        });
        var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
        try
        {
            string source = Path.Combine(fixture.Root, "input-mask.png"), photoB = Path.Combine(fixture.Root, "b.png");
            using (var black = new Mat(303, 201, MatType.CV_8UC1, Scalar.Black)) Cv2.ImWrite(source, black);
            using (var image = new Mat(303, 201, MatType.CV_8UC3, Scalar.Green)) Cv2.ImWrite(photoB, image);
            window.Show();
            var viewport = Descendants<MaskViewport>(window).Single();
            var a = window.Irradiance.ImportMaskForTest(source, "Site A");
            await window.InputsSettled;
            window.Calculate(); await Until(() => Volatile.Read(ref shadeCalls) == 1);
            window.Irradiance.SelectSkyPhotoForTest(photoB);
            Assert.Null(window.Completed); Assert.False(viewport.HasImage);
            Assert.False(window.ExportEnabledForTest);
            Assert.Throws<InvalidOperationException>(() => window.Irradiance.ImportMaskForTest(source, "During switch"));
            release.Set();
            await window.InputsSettled; await Until(() => !window.UpdatingForTest);
            string debugMask = Path.Combine(fixture.Debug, "02-sky-mask", "sky-mask.png");
            Assert.Null(window.Completed); Assert.False(viewport.HasImage); Assert.False(File.Exists(debugMask));
            Assert.False(window.ExportEnabledForTest); Assert.Empty(ManualMaskStore.List(a.PhotoSha256));

            using (var white = new Mat(303, 201, MatType.CV_8UC1, Scalar.White)) Cv2.ImWrite(source, white);
            var b = window.Irradiance.ImportMaskForTest(source, "Site B");
            await window.InputsSettled;
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.Equal(2, shadeCalls); Assert.True(window.ExportEnabledForTest);
            Assert.Equal(b.PhotoSha256, window.Completed!.Mask!.PhotoSha256);
            Assert.Equal(b.Id, window.Completed.Mask.Variant!.Id);
            Assert.Equal(b.PngSha256, AppData.FileKey(debugMask)); Assert.NotEqual(a.PngSha256, b.PngSha256);
            var manifest = AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))!;
            Assert.Equal("Complete", manifest.Status);
            Assert.Equal(b.PhotoSha256, manifest.Inputs["sky-mask"].GetProperty("PhotoSha256").GetString());
        }
        finally { release.Set(); window.Close(); await window.InputsSettled; }
    });

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
            var firstRun = window.Completed!;
            string external = AppServices.Export(firstRun, Path.Combine(fixture.Root, "saved-export"));
            string exportedMask = Path.Combine(external, "02-sky-mask", "sky-mask.png");
            string existingUserFile = Path.Combine(fixture.Debug, "02-sky-mask", "field-note.txt");
            File.WriteAllText(existingUserFile, "Keep this site note.");
            string weatherOutput = Path.Combine(fixture.Debug, "03-irradiance", "horizontal-irradiance.xlsx");
            Assert.True(File.Exists(weatherOutput));
            string[] keep = [first.SkyImage, first.ProfilePath, first.ImportPath, blocked, exportedMask, existingUserFile];
            var originalHashes = keep.ToDictionary(path => path, AppData.FileKey);
            var pv = window.PvAutonomy.Controller;
            pv.SetDraft(PvSettingsDraft.Example()); await pv.EvaluateAsync();
            Assert.True(pv.IsCurrent, pv.Status);
            string savedBattery = Path.Combine(fixture.Debug, "07-battery", "battery-result.json");
            Assert.True(File.Exists(savedBattery));
            string pvNote = Path.Combine(fixture.Debug, "07-battery", "observations.txt");
            File.WriteAllText(pvNote, "Preserve alongside PV outputs.");

            window.Irradiance.SelectSkyPhotoForTest(secondPhoto);
            Assert.Equal(secondPhoto, PortablePaths.Resolve(window.IrradianceController.Settings.SkyImage));
            Assert.Null(window.IrradianceController.Settings.SelectedMaskId);
            Assert.Null(window.Completed); Assert.Null(pv.Result); Assert.False(pv.CanExport);
            Assert.False(viewport.HasImage);
            Assert.False(editorButton.IsEnabled);
            await window.InputsSettled; await pv.CleanupSettled;
            var pending = AppData.Read<DebugDataRun.Manifest>(manifestPath)!;
            Assert.Equal("Stale", pending.Status);
            Assert.Equal(secondPhoto, PortablePaths.Resolve(pending.Settings.SkyImage));
            Assert.Null(pending.MaskEdge); Assert.False(pending.Inputs.ContainsKey("sky-mask"));
            Assert.Null(pending.LastSuccessfulUpdateUtc);
            Assert.Empty(pending.ArtifactHashes); Assert.Empty(pending.Inputs);
            Assert.All(pending.Stages.Values, stage => Assert.Empty(stage.Artifacts));
            Assert.False(File.Exists(weatherOutput));
            Assert.False(File.Exists(maskPath));
            Assert.False(File.Exists(savedBattery));
            Assert.Equal("Preserve alongside PV outputs.", File.ReadAllText(pvNote));
            Assert.Empty(ManualMaskStore.List(imported.PhotoSha256));
            var currentCache = new CurrentValueCache(fixture.Debug);
            foreach (string stage in new[] { "cardinals", "sun-path", "shading" })
                Assert.False(File.Exists(currentCache.PathFor(stage)));
            Assert.True(File.Exists(currentCache.PathFor("solar")));
            foreach (var (path, hash) in originalHashes) Assert.Equal(hash, AppData.FileKey(path));

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
            await pv.EvaluateAsync();
            Assert.True(pv.IsCurrent, pv.Status); Assert.True(File.Exists(savedBattery));
            Assert.Equal("Preserve alongside PV outputs.", File.ReadAllText(pvNote));
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
