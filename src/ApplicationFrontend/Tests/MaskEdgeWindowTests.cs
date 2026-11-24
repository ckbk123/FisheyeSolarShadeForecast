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
    public void TheStudyStartsFromThe95PercentProposalAndUsesOneEditableEdge() => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        // A narrow white ray persists through the cap. Requiring a fully black ring would choose 180°.
        using var raster = new Mat(303, 201, MatType.CV_8UC1, Scalar.Black);
        Cv2.Circle(raster, new OpenCvSharp.Point(100, 151), 76, Scalar.White, -1);
        Cv2.Line(raster, new OpenCvSharp.Point(100, 151), new OpenCvSharp.Point(199, 151), Scalar.White, 1);
        string path = Path.Combine(fixture.Root, "rim.png");
        Cv2.ImWrite(path, raster);
        var selected = ManualMaskStore.Import(fixture.Settings.SkyImage, path, "Visible rim", centeredDisk: true);
        AppData.SaveSettings(fixture.Settings with { SelectedMaskId = selected.Id });
        using var service = new AppServices(fixture.Debug);
        var owner = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false) { ShowActivated = false };
        try
        {
            owner.Show(); await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            var baseline = owner.Completed!;
            var suggestion = baseline.MaskEdge!;
            Assert.Equal(95, suggestion.BlackPercent);
            Assert.InRange(suggestion.EffectiveFullDegrees, 150, 156);
            var dialog = await Open();
            Assert.Empty(Tree(dialog).OfType<RadioButton>()); Assert.Empty(Tree(dialog).OfType<ComboBox>());
            var edit = Find<TextBox>(dialog, "ApparentFullFovDegrees");
            var slider = Find<Slider>(dialog, "ApparentFovSlider");
            Assert.Equal(270, slider.Maximum); Assert.Equal(suggestion.EffectiveFullDegrees, slider.Value);
            var save = Find<Button>(dialog, "SaveRefinedEdge");
            var preview = Tree(dialog).OfType<SkyPhotoViewport>().Single();
            var showMask = Tree(dialog).OfType<CheckBox>().Single();
            showMask.IsChecked = true; Assert.True(preview.Photo.OverlayVisible);
            foreach (string bad in new[] { "", "0", "270.1", "NaN", "-3", "5e-324" })
            {
                edit.Text = bad;
                Assert.False(save.IsEnabled); Assert.Null(preview.EdgeOutline);
            }
            slider.Value = 270; Assert.Equal("270", edit.Text);
            Assert.True(save.IsEnabled); Assert.Equal(270, preview.EdgeOutline!.Bounds.Width, 8);
            var zoom = Find<Button>(dialog, "ZoomMaskEdgeIn");
            Click(zoom); Assert.True(preview.Zoom > 1);
            Click(Tree(dialog).OfType<Button>().Single(b => Equals(b.Content, "Cancel")));
            Assert.Same(baseline, owner.Completed);
            Assert.Null(owner.IrradianceController.Settings.ApparentFullFovDegrees);

            dialog = await Open();
            Find<TextBox>(dialog, "ApparentFullFovDegrees").Text = "215,75";
            Click(Find<Button>(dialog, "SaveRefinedEdge")); await owner.InputsSettled;
            var pinned = AppData.ReadSettings()!;
            Assert.Equal(215.75, pinned.ApparentFullFovDegrees);
            Assert.Equal(new RefinedEdgeSources(AppData.FileKey(fixture.Settings.SkyImage), AppData.FileKey(fixture.Settings.ProfilePath)),
                pinned.ApparentFovSources);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.Equal(215.75, owner.Completed!.MaskEdge!.EffectiveFullDegrees);
            Assert.Equal(215.75, Tree(owner.Irradiance).OfType<SkyPhotoViewport>().Single().EdgeOutline!.Bounds.Width, 8);

            dialog = await Open();
            Assert.Equal(215.75, Find<Slider>(dialog, "ApparentFovSlider").Value);
            // Typing exactly the suggested number still pins that angle until the user chooses Use suggestion.
            var suggested = suggestion.EffectiveFullDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            Find<TextBox>(dialog, "ApparentFullFovDegrees").Text = suggested;
            Click(Find<Button>(dialog, "SaveRefinedEdge")); await owner.InputsSettled;
            dialog = await Open(); Click(Find<Button>(dialog, "SaveRefinedEdge")); await owner.InputsSettled;
            Assert.Equal(suggestion.EffectiveFullDegrees, owner.IrradianceController.Settings.ApparentFullFovDegrees);
            dialog = await Open();
            Click(Tree(dialog).OfType<Button>().Single(b => Equals(b.Content, "Use suggestion")));
            Click(Find<Button>(dialog, "SaveRefinedEdge")); await owner.InputsSettled;
            Assert.Null(owner.IrradianceController.Settings.ApparentFullFovDegrees);
            Assert.Null(AppData.ReadSettings()!.ApparentFovSources);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.Equal(suggestion, owner.Completed!.MaskEdge);
            Assert.Equal(baseline.Run.AfterEnergy, owner.Completed.Run.AfterEnergy);

            async Task<MaskEdgeWindow> Open()
            {
                Click(Find<Button>(owner.Irradiance, "RefineMaskEdge"));
                var opened = owner.OwnedWindows.OfType<MaskEdgeWindow>().Single();
                await Until(() => opened.IsLoaded); await opened.Analysis;
                return opened;
            }
        }
        finally { owner.Close(); }
    });

    [Fact]
    public void PreparingAFreshMaskPreservesTypedInputAndCancelDiscardsChanges() => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        var settings = fixture.Settings;
        var measured = SkyPhotoFieldOfView.Load(settings, 201, 303, null);
        var asset = AppServices.LoadCachedAiMask(settings)!;
        var pending = new TaskCompletionSource<MaskAsset>();
        CancellationToken observed = default;
        double? saved = null;
        var owner = new System.Windows.Window { ShowActivated = false };
        owner.Show();
        try
        {
            var dialog = new MaskEdgeWindow(owner, settings.SkyImage, IrradianceWorkspace.Bitmap(asset.OriginalPreview),
                null, measured, Task.FromResult<AppliedMaskEdge?>(null), ct => { observed = ct; return pending.Task; },
                null, value => saved = value);
            dialog.SetStudyEditable(true); dialog.Show(); await Until(() => dialog.IsLoaded);
            try
            {
                var edit = Find<TextBox>(dialog, "ApparentFullFovDegrees");
                edit.Text = "207";
                pending.SetResult(asset);
                await dialog.Analysis;
                Assert.True(Tree(dialog).OfType<CheckBox>().Single().IsEnabled);
                Assert.Equal(207, Find<Slider>(dialog, "ApparentFovSlider").Value);
                Assert.Contains("180°", Find<TextBlock>(dialog, "EdgeProposal").Text);
                Assert.Null(saved); Assert.Equal(0, fixture.Runs);
            }
            finally { dialog.Close(); }
            Assert.True(observed.IsCancellationRequested); Assert.Null(saved);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelStopsPreparationAndAnUnavailableMaskStillAllowsHandFitting(bool maskFails) => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        var settings = fixture.Settings;
        var asset = AppServices.LoadCachedAiMask(settings)!;
        var owner = new System.Windows.Window { ShowActivated = false };
        owner.Show();
        try
        {
            var pending = new TaskCompletionSource<MaskAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            var measured = SkyPhotoFieldOfView.Load(settings, 201, 303, null);
            double? saved = null;
            var dialog = new MaskEdgeWindow(owner, settings.SkyImage, IrradianceWorkspace.Bitmap(asset.OriginalPreview),
                null, measured, Task.FromResult<AppliedMaskEdge?>(null), ct => pending.Task.WaitAsync(ct),
                null, value => saved = value);
            dialog.SetStudyEditable(true); dialog.Show(); await Until(() => dialog.IsLoaded);
            var analysis = dialog.Analysis;
            Assert.False(analysis.IsCompleted);
            if (maskFails)
            {
                pending.SetException(new IOException("Unavailable test model"));
                await analysis;
                var status = Find<TextBlock>(dialog, "EdgeProposal");
                Assert.Contains("Set the edge on the photo", status.Text);
                Assert.Contains("Unavailable test model", AutomationProperties.GetHelpText(status));
                Find<TextBox>(dialog, "ApparentFullFovDegrees").Text = "180";
                Click(Find<Button>(dialog, "SaveRefinedEdge"));
                Assert.Equal(180d, saved);
            }
            else
            {
                Click(Tree(dialog).OfType<Button>().Single(b => Equals(b.Content, "Cancel")));
                await analysis.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Null(saved);
            }
            Assert.Null(dialog.PreparedMask);
            Assert.Equal(0, fixture.Runs);
        }
        finally { owner.Close(); }
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
    public void FirstPinAfterAnExternalProfileChangeBindsTheReplacement() => Sta(async () =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        using var service = new AppServices(fixture.Debug);
        var owner = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false) { ShowActivated = false };
        try
        {
            owner.Show(); await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            var original = CameraProfileFiles.Load(fixture.Settings.ProfilePath);
            CameraProfileFiles.Export(original with { Calibration = original.Calibration with { MaximumIncidentAngleDegrees = 65 } }, fixture.Root);
            await owner.RecheckSourcesAsync(); await owner.InputsSettled;
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Click(Find<Button>(owner.Irradiance, "RefineMaskEdge"));
            var dialog = owner.OwnedWindows.OfType<MaskEdgeWindow>().Single();
            await Until(() => dialog.IsLoaded); await dialog.Analysis;
            Find<TextBox>(dialog, "ApparentFullFovDegrees").Text = "218";
            Click(Find<Button>(dialog, "SaveRefinedEdge")); await owner.InputsSettled;
            var saved = AppData.ReadSettings()!;
            Assert.Equal(218d, saved.ApparentFullFovDegrees);
            Assert.Equal(AppData.FileKey(fixture.Settings.ProfilePath), saved.ApparentFovSources!.ProfileSha256);
        }
        finally { owner.Close(); }
    });

    [Theory]
    [InlineData("photo")]
    [InlineData("profile")]
    [InlineData("moved")]
    [InlineData("temporarily missing")]
    public void ReopeningVerifiesThePhotoAndProfileThatOwnASavedRefinement(string change) => Sta(() =>
    {
        using var fixture = new Fixture();
        Setup120DegreeProfile(fixture);
        // Older settings contain only the angle. The first launch preserves and binds that fit.
        AppData.SaveSettings(fixture.Settings with { ApparentFullFovDegrees = 137.5 });
        StartAndCheck(137.5);
        var saved = AppData.ReadSettings()!;
        var original = new RefinedEdgeSources(AppData.FileKey(fixture.Settings.SkyImage), AppData.FileKey(fixture.Settings.ProfilePath));
        Assert.Equal(original, saved.ApparentFovSources);

        if (change == "photo")
        {
            using var replacement = new Mat(303, 201, MatType.CV_8UC3, Scalar.Green);
            Cv2.ImWrite(fixture.Settings.SkyImage, replacement);
        }
        else if (change == "profile")
        {
            var measured = CameraProfileFiles.Load(fixture.Settings.ProfilePath);
            CameraProfileFiles.Export(measured with { Calibration = measured.Calibration with { MaximumIncidentAngleDegrees = 65 } }, fixture.Root);
        }
        else if (change == "moved")
        {
            var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "relocated")).FullName;
            string copy = Path.Combine(folder, Path.GetFileName(saved.ProfilePath));
            File.Copy(fixture.Settings.ProfilePath, copy);
            AppData.SaveSettings(saved with { ProfilePath = copy });
        }
        else
        {
            File.Move(fixture.Settings.ProfilePath, fixture.Settings.ProfilePath + ".offline");
        }
        bool keep = change is "moved" or "temporarily missing";
        StartAndCheck(keep ? 137.5 : null);
        Assert.Equal(keep ? original : null, AppData.ReadSettings()!.ApparentFovSources);
        Assert.Equal(0, fixture.Runs);
        return Task.CompletedTask;

        void StartAndCheck(double? angle)
        {
            using var service = new AppServices(fixture.Debug);
            var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
            try { Assert.Equal(angle, window.IrradianceController.Settings.ApparentFullFovDegrees); }
            finally { window.Close(); }
        }
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
