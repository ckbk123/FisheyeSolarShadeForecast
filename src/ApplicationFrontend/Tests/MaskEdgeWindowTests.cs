using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenCvSharp;
using SolarShade.Camera;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class MaskEdgeWindowTests
{
    [Fact]
    public void StudyAndIndependentMasksUseTheirOwnCoverageWithoutChangingTheStudy() => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        using var services = new AppServices(fixture.Debug);
        var owner = new MainWindow(applicationServices: services, loadExampleOnFirstRun: false) { ShowActivated = false };
        try
        {
            var rendered = new TaskCompletionSource();
            owner.ContentRendered += (_, _) => rendered.TrySetResult(); owner.Show(); await rendered.Task;
            await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            var entry = Assert.Single(Tree(owner.Irradiance).OfType<Button>(), b => Equals("Estimate edge…", b.Content));
            var dialog = await Open();
            var status = Assert.Single(Tree(dialog).OfType<TextBlock>(), t => AutomationProperties.GetAutomationId(t) == "MaskEdgeResult");
            var sky = Assert.Single(Tree(dialog).OfType<TextBlock>(), t => AutomationProperties.GetAutomationId(t) == "MaskEdgeOuterSky");
            var threshold = Tree(dialog).OfType<ComboBox>().Single();
            Assert.Equal(new object[] { 80, 90, 95, 100 }, threshold.Items.Cast<object>());
            Assert.Equal(100, threshold.SelectedItem);
            Assert.Contains("180° scan cap", status.Text);
            Assert.Contains("Open sky sampled beyond calibration", sky.Text);
            Assert.Contains("observed black", AutomationProperties.GetHelpText(status));
            var showMask = Tree(dialog).OfType<CheckBox>().Single();
            var view = Tree(dialog).OfType<SkyPhotoViewport>().Single();
            var apply = Tree(dialog).OfType<Button>().Single(b => Equals(b.Content, "Use for calculation"));
            Assert.True(apply.IsEnabled);
            Assert.True(showMask.IsEnabled); Assert.True(view.HasCalibratedOutline);
            showMask.IsChecked = true; Assert.True(view.Photo.OverlayVisible);

            string maskPath = Path.Combine(fixture.Root, "independent.png");
            using (var mask = new Mat(303, 201, MatType.CV_8UC1, Scalar.Black))
            {
                Cv2.Circle(mask, new OpenCvSharp.Point(100, 151), 79, Scalar.White, -1);
                Cv2.ImWrite(maskPath, mask);
            }
            await dialog.SelectMask(maskPath);
            Assert.Contains("All-black band", status.Text); Assert.Contains("160° extrapolated span", status.Text);
            Assert.False(apply.IsEnabled);
            var manual = Tree(dialog).OfType<RadioButton>().Single(b => AutomationProperties.GetAutomationId(b) == "ManualApparentFovMode");
            manual.IsChecked = true;
            Assert.NotNull(view.ManualOutline); Assert.False(apply.IsEnabled);
            Tree(dialog).OfType<RadioButton>().Single(b => AutomationProperties.GetAutomationId(b) == "AutomaticMaskEdgeMode").IsChecked = true;
            string skyFinding = sky.Text; Assert.Contains("Open sky sampled beyond calibration", skyFinding);
            threshold.SelectedItem = 95; Assert.Contains("Approximate 95%", status.Text); Assert.Equal(skyFinding, sky.Text);
            Assert.True(view.Photo.OverlayVisible);
            var zoom = Assert.Single(Tree(dialog).OfType<Button>(), b => AutomationProperties.GetAutomationId(b) == "ZoomMaskEdgeIn");
            zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(view.Zoom > 1);

            string copy = Path.Combine(fixture.Root, "copy.png");
            File.WriteAllBytes(copy, AppServices.LoadCachedAiMask(fixture.Settings)!.Png);
            await dialog.SelectMask(copy);
            Assert.Contains(Tree(dialog).OfType<TextBlock>(), t => t.Text.Contains("Matches study mask"));
            Assert.Contains("180° scan cap", status.Text);
            string reencoded = Path.Combine(fixture.Root, "three-channel-copy.png");
            using (var rgb = Cv2.ImRead(copy, ImreadModes.Color)) Cv2.ImWrite(reencoded, rgb);
            Assert.False(File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(reencoded)));
            await dialog.SelectMask(reencoded);
            Assert.Contains(Tree(dialog).OfType<TextBlock>(), t => t.Text.Contains("Matches study mask"));
            string blackMask = Path.Combine(fixture.Root, "all-black.png");
            using (var black = new Mat(303, 201, MatType.CV_8UC1, Scalar.Black)) Cv2.ImWrite(blackMask, black);
            await dialog.SelectMask(blackMask); Assert.Equal("All sampled directions beyond calibration are blocked.", sky.Text);
            string invalidMask = Path.Combine(fixture.Root, "wrong-size.png");
            using (var invalid = new Mat(2, 2, MatType.CV_8UC1, Scalar.Black)) Cv2.ImWrite(invalidMask, invalid);
            Task previous = dialog.SelectMask(maskPath);
            await dialog.SelectMask(invalidMask); await previous;
            Assert.Equal("Could not analyze this mask.", status.Text);
            Assert.Empty(sky.Text);
            Assert.False(view.Photo.OverlayVisible); Assert.False(showMask.IsEnabled);
            Assert.Contains(Tree(dialog).OfType<TextBlock>(), t => t.Text.Contains("201 × 303"));
            var reset = Assert.Single(Tree(dialog).OfType<Button>(), b => Equals("Use study mask", b.Content));
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await dialog.Analysis;
            Assert.Contains("180° scan cap", status.Text); Assert.True(showMask.IsEnabled); Assert.True(apply.IsEnabled);

            string nextPhoto = Path.Combine(fixture.Root, "second-photo.png");
            using (var image = new Mat(303, 201, MatType.CV_8UC3, Scalar.Gray)) Cv2.ImWrite(nextPhoto, image);
            owner.Irradiance.SelectSkyPhotoForTest(nextPhoto); await owner.InputsSettled;
            Assert.False(dialog.IsVisible);
            var second = await Open();
            Assert.Contains(Tree(second).OfType<TextBlock>(), t => t.Text == "Choose a full-frame mask to try the ring scan.");
            await second.SelectMask(maskPath);
            Assert.Contains(Tree(second).OfType<TextBlock>(), t => t.Text.Contains("All-black band"));
            var original = CameraProfileFiles.Load(fixture.Settings.ProfilePath);
            CameraProfileFiles.Export(original with { Calibration = original.Calibration with { MaximumIncidentAngleDegrees = 117.5 } }, fixture.Root);
            await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            Assert.False(second.IsVisible);
            var wide = await Open();
            Assert.Contains(Tree(wide).OfType<TextBlock>(), t => t.Text.Contains("Calibration already covers the 180° scan cap"));
            Assert.Contains(Tree(wide).OfType<TextBlock>(), t => t.Text.Contains("235°"));
            Assert.False(Tree(wide).OfType<Button>().Single(b => Equals(b.Content, "Use for calculation")).IsEnabled);
            Tree(wide).OfType<RadioButton>().Single(b => AutomationProperties.GetAutomationId(b) == "ManualApparentFovMode").IsChecked = true;
            var angle = Tree(wide).OfType<TextBox>().Single();
            Assert.Equal("235", angle.Text);
            angle.Text = "210";
            var useApparent = Tree(wide).OfType<Button>().Single(b => Equals(b.Content, "Use for calculation"));
            Assert.True(useApparent.IsEnabled); // The newly selected photo has no prepared mask yet.
            useApparent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await owner.InputsSettled;
            Assert.Equal(210, owner.IrradianceController.Settings.ApparentFullFovDegrees);
            owner.Irradiance.SelectSkyPhotoForTest(fixture.Settings.SkyImage); await owner.InputsSettled;
            Assert.Null(owner.IrradianceController.Settings.ApparentFullFovDegrees);
            File.WriteAllText(fixture.Settings.ProfilePath, "broken camera profile");
            await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            Assert.False(wide.IsVisible); Assert.False(entry.IsEnabled);
            Assert.Null(owner.IrradianceController.Settings.SelectedMaskId); Assert.Null(owner.Completed);
            Assert.Null(owner.IrradianceController.Settings.MaskEdgeThresholdPercent);
            Assert.Equal(0, services.CardinalGenerationCount); Assert.Equal(0, fixture.Runs);

            async Task<MaskEdgeWindow> Open()
            {
                Assert.True(entry.IsEnabled);
                entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var window = Assert.Single(owner.OwnedWindows.OfType<MaskEdgeWindow>());
                await Until(() => window.IsLoaded); await window.Analysis;
                return window;
            }
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void ApparentFovIsPreviewedAtNativeScaleAndAppliedOnlyOnRequest() => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        using var services = new AppServices(fixture.Debug);
        var owner = new MainWindow(applicationServices: services, loadExampleOnFirstRun: false) { ShowActivated = false };
        try
        {
            var rendered = new TaskCompletionSource();
            owner.ContentRendered += (_, _) => rendered.TrySetResult(); owner.Show(); await rendered.Task;
            await owner.RecheckSourcesAsync(); owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            var baseline = owner.Completed!;
            var entry = Tree(owner.Irradiance).OfType<Button>().Single(b => Equals(b.Content, "Estimate edge…"));
            var mainView = Tree(owner.Irradiance).OfType<SkyPhotoViewport>().Single();
            var dialog = await Open();
            var edit = Find<TextBox>(dialog, "ApparentFullFovDegrees");
            var slider = Find<Slider>(dialog, "ApparentFovSlider");
            var apply = Find<Button>(dialog, "ApplyMaskEdge");
            var preview = Tree(dialog).OfType<SkyPhotoViewport>().Single();
            Find<RadioButton>(dialog, "ManualApparentFovMode").IsChecked = true;
            Assert.Equal("180", edit.Text);
            slider.Value = 156;
            Assert.Equal("156", edit.Text); Assert.Equal(156, preview.ManualOutline!.Bounds.Width, 9);
            Tree(dialog).OfType<CheckBox>().Single().IsChecked = true;
            Assert.True(preview.Photo.OverlayVisible); Assert.Equal(156, preview.ManualOutline.Bounds.Width, 9);
            foreach (string bad in new[] { "", "0", "181", "NaN", "-3", "5e-324" })
            {
                edit.Text = bad;
                Assert.False(apply.IsEnabled); Assert.Null(preview.ManualOutline);
            }
            edit.Text = "105,25";
            Assert.Equal(105.25, slider.Value); Assert.Equal(105.25, preview.ManualOutline!.Bounds.Width, 9);
            Assert.True(apply.IsEnabled); Assert.Same(baseline, owner.Completed); Assert.Null(mainView.ManualOutline);
            Assert.Null(owner.IrradianceController.Settings.ApparentFullFovDegrees);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await owner.InputsSettled;
            Assert.Equal(105.25, AppData.ReadSettings()!.ApparentFullFovDegrees);
            Assert.Null(AppData.ReadSettings()!.MaskEdgeThresholdPercent);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.Equal(105.25, owner.Completed!.MaskEdge!.ManualFullFovDegrees);
            Assert.Equal(105.25, mainView.ManualOutline!.Bounds.Width, 9);
            Assert.Null(mainView.CapOutline);

            dialog = await Open();
            Assert.True(Find<RadioButton>(dialog, "ManualApparentFovMode").IsChecked);
            Assert.Equal(105.25, Find<Slider>(dialog, "ApparentFovSlider").Value);
            Find<RadioButton>(dialog, "AutomaticMaskEdgeMode").IsChecked = true;
            Assert.Null(Tree(dialog).OfType<SkyPhotoViewport>().Single().ManualOutline);
            Find<Button>(dialog, "ApplyMaskEdge").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await owner.InputsSettled;
            Assert.Null(owner.IrradianceController.Settings.ApparentFullFovDegrees);
            Assert.Equal(100, owner.IrradianceController.Settings.MaskEdgeThresholdPercent);

            async Task<MaskEdgeWindow> Open()
            {
                entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var opened = owner.OwnedWindows.OfType<MaskEdgeWindow>().Single();
                await Until(() => opened.IsLoaded); await opened.Analysis;
                return opened;
            }
        }
        finally { owner.Close(); }
    });

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Tree(root).OfType<T>().Single(e => AutomationProperties.GetAutomationId(e) == id);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacingAStudySourceAtTheSamePathClearsItsHandFit(bool changePhoto) => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        AppData.SaveSettings(fixture.Settings with { ApparentFullFovDegrees = 137.5 });
        using var services = new AppServices(fixture.Debug);
        var owner = new MainWindow(applicationServices: services, loadExampleOnFirstRun: false) { ShowActivated = false };
        try
        {
            var rendered = new TaskCompletionSource();
            owner.ContentRendered += (_, _) => rendered.TrySetResult(); owner.Show(); await rendered.Task;
            await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            Assert.Equal(137.5, owner.IrradianceController.Settings.ApparentFullFovDegrees);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.Equal(137.5, owner.Completed!.MaskEdge!.ManualFullFovDegrees);
            if (changePhoto)
            {
                using var replacement = new Mat(303, 201, MatType.CV_8UC3, Scalar.Green);
                Cv2.ImWrite(fixture.Settings.SkyImage, replacement);
            }
            else
            {
                var original = CameraProfileFiles.Load(fixture.Settings.ProfilePath);
                CameraProfileFiles.Export(original with { Calibration = original.Calibration with { MaximumIncidentAngleDegrees = 65 } }, fixture.Root);
            }
            await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            Assert.Null(owner.IrradianceController.Settings.ApparentFullFovDegrees);
            Assert.Null(AppData.ReadSettings()!.ApparentFullFovDegrees);
            Assert.False(owner.ExportEnabledForTest);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void ApplyingTheStudyMaskChangesTheNextCalculationAndCanBeReset() => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        using var services = new AppServices(fixture.Debug);
        var owner = new MainWindow(applicationServices: services, loadExampleOnFirstRun: false) { ShowActivated = false };
        try
        {
            var rendered = new TaskCompletionSource();
            owner.ContentRendered += (_, _) => rendered.TrySetResult(); owner.Show(); await rendered.Task;
            await owner.RecheckSourcesAsync(); owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.True(owner.ExportEnabledForTest); Assert.Null(owner.Completed!.MaskEdge);
            var baseline = owner.Completed;
            var view = Tree(owner.Irradiance).OfType<SkyPhotoViewport>().Single();
            var entry = Tree(owner.Irradiance).OfType<Button>().Single(b => Equals("Estimate edge…", b.Content));
            var readout = Tree(owner.Irradiance).OfType<TextBlock>().Single(t => AutomationProperties.GetAutomationId(t) == "CalculationFieldOfView");
            Click(entry);
            var dialog = owner.OwnedWindows.OfType<MaskEdgeWindow>().Single();
            await Until(() => dialog.IsLoaded); await dialog.Analysis;
            var apply = Tree(dialog).OfType<Button>().Single(b => Equals("Use for calculation", b.Content));
            Assert.True(apply.IsEnabled); Click(apply); await owner.InputsSettled;
            Assert.False(dialog.IsVisible); Assert.False(owner.ExportEnabledForTest);
            Assert.Equal(100, AppData.ReadSettings()!.MaskEdgeThresholdPercent); Assert.Same(baseline, owner.Completed);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.True(owner.ExportEnabledForTest);
            Assert.Equal(120, owner.Completed!.MaskEdge!.MeasuredFullDegrees, 7);
            Assert.Equal(180, owner.Completed.MaskEdge.EffectiveFullDegrees, 7);
            Assert.False(owner.Completed.MaskEdge.ObservedTerminalBand); Assert.Contains("180°", readout.Text);
            Assert.NotNull(view.CapOutline);
            var reset = Tree(owner.Irradiance).OfType<Button>().Single(b => Equals("Use calibration only", b.Content));
            Click(reset); await owner.InputsSettled;
            Assert.Null(AppData.ReadSettings()!.MaskEdgeThresholdPercent);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.True(owner.ExportEnabledForTest); Assert.Null(owner.Completed!.MaskEdge);
            Assert.Null(view.CapOutline);
            Assert.Equal(baseline.Run.AfterEnergy, owner.Completed.Run.AfterEnergy);
        }
        finally { owner.Close(); }

        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    });

    private static void Setup120DegreeProfile(Fixture fixture)
    {
        var profile = CameraProfileFiles.Load(fixture.Settings.ProfilePath);
        CameraProfileFiles.Export(profile with { Calibration = profile.Calibration with
            { IncidentAngleToRadiusPolynomial = [0, 180 / Math.PI], MaximumIncidentAngleDegrees = 60 } }, fixture.Root);
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Tree(child)) yield return nested;
    }
}
