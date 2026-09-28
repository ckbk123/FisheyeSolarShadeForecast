using System.IO;
using System.Text.Json;
using SolarShade.Desktop;
using SolarShade.Shading;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class DependencyTests
{
    public static IEnumerable<object[]> Fields()
    {
        yield return ["SkyImage", "other.png", ArtifactGroup.Profile | ArtifactGroup.Mask | InputDependencies.PoseChain];
        yield return ["ProfilePath", "other.json", ArtifactGroup.Profile | InputDependencies.PoseChain];
        yield return ["CoverageAngle", 100d, ArtifactGroup.Profile | InputDependencies.PoseChain];
        yield return ["Model", SolarShade.SkyPhotoMasking.SkyModel.EfficientNetB4, ArtifactGroup.Mask | InputDependencies.PoseChain];
        yield return ["Resolution", 512, ArtifactGroup.Mask | InputDependencies.PoseChain];
        yield return ["CenteredDisk", true, ArtifactGroup.Mask | InputDependencies.PoseChain];
        yield return ["BottomAzimuth", 90d, InputDependencies.PoseChain];
        yield return ["CameraTilt", 15d, InputDependencies.PoseChain];
        yield return ["CameraRoll", 25d, InputDependencies.PoseChain];
        yield return ["PanelTilt", 45d, InputDependencies.PanelChain];
        yield return ["PanelAzimuth", 90d, InputDependencies.PanelChain];
        yield return ["Isotropic", true, InputDependencies.PanelChain];
        yield return ["Latitude", 11d, InputDependencies.GeometryChain];
        yield return ["Longitude", 107d, InputDependencies.GeometryChain];
        yield return ["Elevation", 100d, InputDependencies.GeometryChain];
        yield return ["Start", new DateTime(2025, 5, 2), InputDependencies.GeometryChain];
        yield return ["End", new DateTime(2025, 5, 30), InputDependencies.GeometryChain];
        yield return ["Substeps", 15, InputDependencies.GeometryChain];
        yield return ["Zone", "UTC", InputDependencies.WeatherChain];
        yield return ["ImportPath", "other.csv", InputDependencies.WeatherChain];
        yield return ["ImportWindow", 1, InputDependencies.WeatherChain];
        yield return ["ImportIntervalMinutes", 15d, InputDependencies.WeatherChain];
        yield return ["Provider", 1, ArtifactGroup.None];
        yield return ["Columns", 7, ArtifactGroup.None];
        yield return ["Rows", 10, ArtifactGroup.None];
        yield return ["SquareMm", 25d, ArtifactGroup.None];
        yield return ["CalibrationFolder", "other", ArtifactGroup.None];
        yield return ["ShowSunPath", false, ArtifactGroup.None];
        yield return ["ShowCardinalDirections", false, ArtifactGroup.None];
        yield return ["UseSystemTimeZone", false, ArtifactGroup.None];
    }

    [Theory][MemberData(nameof(Fields))]
    public void EachInputInvalidatesOnlyItsDependentArtifacts(string field, object value, ArtifactGroup expected)
    {
        var original = new UserSettings { ImportPath = "weather.csv", Zone = "SE Asia Standard Time" };
        var edited = original with { };
        typeof(UserSettings).GetProperty(field)!.SetValue(edited, value);
        Assert.Equal(expected, InputDependencies.Capture(original).Difference(InputDependencies.Capture(edited)));
        Assert.Equal(expected, InputDependencies.ForField(field, original));
    }

    [Fact]
    public void ApiInputsIgnoreImportFallbackButIncludeProviderAndSite()
    {
        var settings = new UserSettings(); var before = InputDependencies.Capture(settings);
        Assert.Equal(ArtifactGroup.None, before.Difference(InputDependencies.Capture(settings with { ImportWindow = 1, ImportIntervalMinutes = 15 })));
        Assert.Equal(InputDependencies.WeatherChain, before.Difference(InputDependencies.Capture(settings with { Latitude = 45 })));
        Assert.Equal(InputDependencies.WeatherChain, before.Difference(InputDependencies.Capture(settings with { Provider = 1 })));
    }

    private static Dictionary<string, string> Artifacts(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != "run.json").ToDictionary(p => Path.GetRelativePath(root, p), AppData.FileKey);
    private static void AssertRemovedOnly(string root, Dictionary<string, string> before, ArtifactGroup removed)
    {
        Assert.NotEmpty(before);
        foreach (var (relative, hash) in before)
        {
            string file = Path.Combine(root, relative);
            if ((InputDependencies.ForArtifact(relative) & removed) != 0) Assert.False(File.Exists(file), relative);
            else { Assert.True(File.Exists(file), relative); Assert.Equal(hash, AppData.FileKey(file)); }
        }
    }
    private static Task<Evaluation> Calculate(AppServices service, UserSettings settings) => service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);

    [Fact]
    public void CameraEditsPreserveMaskAndNumericalFilesAndCannotReviveDeletedArtifacts() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            var result = window.Completed!; var before = Artifacts(result.DebugDirectory!);
            Assert.NotNull(window.CardinalsForTest); Assert.True(window.SunPathVisibleForTest);
            window.EditFieldForTest("CameraTilt", "10");
            AssertRemovedOnly(result.DebugDirectory!, before, InputDependencies.PoseChain);
            Assert.Null(window.CardinalsForTest); Assert.False(window.SunPathVisibleForTest);
            string manifest = Path.Combine(result.DebugDirectory!, "run.json");
            var firstWrite = File.GetLastWriteTimeUtc(manifest); var firstBytes = File.ReadAllBytes(manifest);
            for (int i = 0; i < 100; i++) window.EditFieldForTest("CameraTilt", (i % 90).ToString());
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(manifest)); Assert.Equal(firstBytes, File.ReadAllBytes(manifest));
            window.EditFieldForTest("CameraTilt", "0"); window.RecheckSources();
            Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest); Assert.False(window.ExportEnabledForTest);
            Assert.Throws<InvalidOperationException>(() => AppServices.Export(result, Path.Combine(fixture.Root, "exports")));
            Assert.Equal(1, fixture.Runs);
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest); Assert.True(window.ExportEnabledForTest);
            Assert.Equal(result.Run.BeforeEnergy, window.Completed!.Run.BeforeEnergy);
            Assert.Equal(result.Run.AfterEnergy, window.Completed.Run.AfterEnergy);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PanelDraftClearsOnlyPanelFilesAndFutureCalibrationDoesNotDirtyResults() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            var result = window.Completed!; var before = Artifacts(result.DebugDirectory!); var cardinals = window.CardinalsForTest;
            window.EditFieldForTest("Columns", "invalid"); window.EditFieldForTest("SquareMm", "30");
            Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest); Assert.True(window.ExportEnabledForTest);
            AssertRemovedOnly(result.DebugDirectory!, before, ArtifactGroup.None);
            window.EditFieldForTest("PanelTilt", "-");
            Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest);
            AssertRemovedOnly(result.DebugDirectory!, before, InputDependencies.PanelChain);
            Assert.Same(cardinals, window.CardinalsForTest); Assert.True(window.SunPathVisibleForTest);
            window.EditFieldForTest("PanelTilt", fixture.Settings.PanelTilt.ToString());
            Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest); Assert.False(window.ExportEnabledForTest);
        }
        finally { window.Close(); }
    });

    [Theory][InlineData("SkyImage")][InlineData("ProfilePath")][InlineData("ImportPath")]
    public async Task SourceReplacementWithRestoredTimestampInvalidatesCorrectArtifacts(string field)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings); var before = Artifacts(result.DebugDirectory!);
        string path = (string)typeof(UserSettings).GetProperty(field)!.GetValue(fixture.Settings)!;
        var stamp = File.GetLastWriteTimeUtc(path); byte[] contents = File.ReadAllBytes(path);
        contents[^1] ^= 1; File.WriteAllBytes(path, contents); File.SetLastWriteTimeUtc(path, stamp);
        var changed = service.ObserveInputs(fixture.Settings, refreshSources: true, verifyArtifacts: true);
        Assert.Equal(InputDependencies.ForField(field, fixture.Settings), changed);
        AssertRemovedOnly(result.DebugDirectory!, before, changed);
        Assert.False(service.IsCurrent(result)); Assert.Equal(1, fixture.Runs);
        Assert.Throws<InvalidOperationException>(() => AppServices.Export(result, Path.Combine(fixture.Root, "exports")));
    }

    [Fact]
    public async Task ExportRechecksSourceWithoutAnInterfaceNotification()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        File.AppendAllText(fixture.Settings.ImportPath, "\n");
        string exports = Path.Combine(fixture.Root, "exports");
        Assert.Throws<InvalidOperationException>(() => AppServices.Export(result, exports));
        Assert.False(Directory.Exists(exports)); Assert.False(service.IsCurrent(result));
    }

    [Fact]
    public void FileNotificationMarksResultsStaleWithoutCalculating() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            File.AppendAllText(fixture.Settings.ImportPath, "\n");
            await Until(() => window.UpdateStateForTest == UpdateState.UpdateRequired);
            Assert.False(window.ExportEnabledForTest); Assert.Equal(1, fixture.Runs);
        }
        finally { window.Close(); }
    });

    [Theory][InlineData(false)][InlineData(true)]
    public async Task MissingOrTamperedArtifactsInvalidateDownstreamFiles(bool tamper)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings); var before = Artifacts(result.DebugDirectory!);
        string raw = Path.Combine(result.DebugDirectory!, "03-irradiance", "horizontal-irradiance.xlsx");
        if (tamper) File.AppendAllText(raw, "changed"); else File.Delete(raw);
        Assert.Throws<InvalidOperationException>(() => AppServices.Export(result, Path.Combine(fixture.Root, "exports")));
        Assert.Equal(InputDependencies.WeatherChain, service.ObserveInputs(fixture.Settings, verifyArtifacts: true));
        AssertRemovedOnly(result.DebugDirectory!, before, InputDependencies.WeatherChain);
        Assert.False(service.IsCurrent(result));
    }

    [Fact]
    public void LockedDebugFileReportsFailureThenRetriesCleanup() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            string debug = window.Completed!.DebugDirectory!; var before = Artifacts(debug);
            using (var locked = new FileStream(Path.Combine(debug, "05-transposition", "panel-unshaded.xlsx"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                window.EditFieldForTest("PanelTilt", "45");
                Assert.Equal(UpdateState.Failed, window.UpdateStateForTest); Assert.False(window.ExportEnabledForTest);
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(debug, "run.json")));
                Assert.Equal("Stale", manifest.RootElement.GetProperty("Status").GetString());
            }
            window.RecheckSources(); AssertRemovedOnly(debug, before, InputDependencies.PanelChain);
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task MissingManifestCannotLeaveTheResultCurrent()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        File.Delete(Path.Combine(result.DebugDirectory!, "run.json"));
        Assert.Equal(ArtifactGroup.All, service.ObserveInputs(fixture.Settings, verifyArtifacts: true));
        Assert.False(service.IsCurrent(result)); Assert.Empty(Artifacts(result.DebugDirectory!));
    }

    [Fact]
    public void CleanupNeverDeletesASelectedInputInsideItsRun()
    {
        using var fixture = new Fixture(); using var run = new DebugDataRun(fixture.Settings, "calibration", fixture.Debug);
        string stage = run.BeginStage("01-calibration", "Fixture", typeof(AppServices));
        string input = Path.Combine(stage, "camera-profile.json"); File.WriteAllText(input, "selected profile");
        run.CompleteStage("01-calibration", [input]); run.Complete();
        input = Path.Combine(run.DirectoryPath, "01-calibration", "camera-profile.json");
        Assert.Throws<IOException>(() => run.Invalidate(ArtifactGroup.Profile, [input]));
        Assert.Equal("selected profile", File.ReadAllText(input)); Assert.False(run.IsCurrent);
        string durable = Path.Combine(fixture.Root, "saved-profile.json"); File.Copy(input, durable);
        run.Invalidate(ArtifactGroup.Profile, [durable]);
        Assert.False(File.Exists(input)); Assert.Equal("selected profile", File.ReadAllText(durable));
    }

    [Fact]
    public void ObsoleteWriterCannotRecreateInvalidatedFiles() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var release = new ManualResetEventSlim(); int entered = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, _) =>
        {
            Interlocked.Increment(ref entered);
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            return ShadingCorrectionModule.ApplyToPanel(input, scene, CancellationToken.None);
        });
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => Volatile.Read(ref entered) == 1);
            string debug = fixture.Debug;
            var before = Artifacts(debug);
            window.EditFieldForTest("PanelTilt", "45"); release.Set();
            await Until(() => !window.UpdatingForTest);
            AssertRemovedOnly(debug, before, InputDependencies.PanelChain);
            Assert.False(Directory.Exists(Path.Combine(debug, "06-shading")) && Directory.GetFiles(Path.Combine(debug, "06-shading")).Length > 0);
            Assert.Null(window.Completed); Assert.False(window.ExportEnabledForTest);
        }
        finally { release.Set(); window.Close(); }
    });
}
