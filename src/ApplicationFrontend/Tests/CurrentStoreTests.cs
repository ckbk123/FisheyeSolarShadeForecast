using System.IO;
using System.Text.Json;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class CurrentStoreTests
{
    private static Task<Evaluation> Calculate(AppServices service, UserSettings settings) => service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
    private static Dictionary<string, (string Hash, DateTime Modified)> Artifacts(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != "run.json").ToDictionary(p => Path.GetRelativePath(root, p), p => (AppData.FileKey(p), File.GetLastWriteTimeUtc(p)));
    private static void AssertPreserved(string root, Dictionary<string, (string Hash, DateTime Modified)> before, ArtifactGroup except = ArtifactGroup.None)
    {
        foreach (var (relative, expected) in before.Where(p => (InputDependencies.ForArtifact(p.Key) & except) == 0))
        {
            string file = Path.Combine(root, relative);
            Assert.Equal(expected.Hash, AppData.FileKey(file)); Assert.Equal(expected.Modified, File.GetLastWriteTimeUtc(file));
        }
    }
    [Fact]
    public async Task TwentyUpdatesKeepOneSetAndPreserveIndependentFilesExactly()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var first = await Calculate(service, fixture.Settings); var before = Artifacts(fixture.Debug);
        for (int i = 1; i < 20; i++)
        {
            var result = await Calculate(service, fixture.Settings with { PanelTilt = i });
            Assert.Equal(fixture.Debug, result.DebugDirectory); Assert.True(service.IsCurrent(result));
            AssertPreserved(fixture.Debug, before, InputDependencies.PanelChain);
            Assert.False(Directory.Exists(fixture.Debug + ".staging"));
            Assert.Equal(7, Directory.GetDirectories(fixture.Debug).Length);
            Assert.Equal(before.Count, Artifacts(fixture.Debug).Count);
        }
        Assert.Equal(20, fixture.Runs); Assert.False(service.IsCurrent(first));
        Assert.Throws<InvalidOperationException>(() => AppServices.Export(first, Path.Combine(fixture.Root, "old-export")));
        var current = AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))!;
        Assert.NotNull(current.LastSuccessfulUpdateUtc); Assert.Equal("Complete", current.Status);
    }
    [Fact]
    public async Task RestartAndUnchangedUpdateReuseValuesWithoutRewritingArtifacts()
    {
        using var fixture = new Fixture(); Evaluation first;
        using (var service = new AppServices(fixture.Debug)) first = await Calculate(service, fixture.Settings);
        var before = Artifacts(fixture.Debug);
        using var restarted = new AppServices(fixture.Debug, (_, _, _) => throw new Exception("Valid shading should be reused after restart."));
        var result = await Calculate(restarted, fixture.Settings);
        Assert.Equal(first.Run.Rows, result.Run.Rows); Assert.Equal(first.Run.AfterEnergy, result.Run.AfterEnergy);
        Assert.True(result.PreparationCount == 0, "Solar timeline was recomputed.");
        Assert.True(result.CardinalGenerationCount == 0, "Cardinal overlay was recomputed.");
        Assert.True(result.SunPathGenerationCount == 0, "Sun path was recomputed.");
        AssertPreserved(fixture.Debug, before); Assert.True(restarted.IsCurrent(result));
    }
    [Fact]
    public async Task CameraUpdateRewritesSunPathButPreservesNumericalSolarWorkbooks()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        await Calculate(service, fixture.Settings); var before = Artifacts(fixture.Debug);
        await Calculate(service, fixture.Settings with { CameraTilt = 15 });
        AssertPreserved(fixture.Debug, before, InputDependencies.PoseChain);
        Assert.False(Directory.Exists(fixture.Debug + ".staging"));
    }
    [Fact]
    public async Task SecondOwnerIsRejectedUntilFirstOwnerCloses()
    {
        using var fixture = new Fixture();
        using (var first = new AppServices(fixture.Debug))
        {
            await Calculate(first, fixture.Settings);
            using var second = new AppServices(fixture.Debug);
            var error = await Assert.ThrowsAsync<IOException>(() => Calculate(second, fixture.Settings));
            Assert.Contains("already in use", error.Message);
            Assert.Throws<IOException>(() => DebugDataRun.CopyCompleted(fixture.Debug, Path.Combine(fixture.Root, "outside-export")));
        }
        using var reopened = new AppServices(fixture.Debug);
        Assert.True(reopened.IsCurrent(await Calculate(reopened, fixture.Settings)));
    }
    [Fact]
    public async Task InterruptedPublicationRemovesPartialFilesAndKeepsIndependentArtifacts()
    {
        using var fixture = new Fixture();
        using (var service = new AppServices(fixture.Debug)) await Calculate(service, fixture.Settings);
        var before = Artifacts(fixture.Debug);
        string manifest = Path.Combine(fixture.Debug, "run.json");
        var state = AppData.Read<DebugDataRun.Manifest>(manifest)!;
        state.Status = "Running"; state.Invalidated |= InputDependencies.PanelChain;
        state.PendingGroups["05-transposition"] = ArtifactGroup.Transposition;
        state.Stages["05-transposition"] = state.Stages["05-transposition"] with { Status = "Running" };
        AppData.Write(manifest, state);
        File.WriteAllText(Path.Combine(fixture.Debug, "05-transposition", "partial.xlsx"), "interrupted write");
        string staging = fixture.Debug + ".staging"; Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, ".solarshade-staging"), "SolarShade current dataset staging v1");
        File.WriteAllText(Path.Combine(staging, "incomplete.tmp"), "partial");
        using var restarted = new AppServices(fixture.Debug);
        restarted.PrepareInputs(fixture.Settings);
        Assert.Equal("Interrupted", AppData.Read<DebugDataRun.Manifest>(manifest)!.Status);
        Assert.False(Directory.Exists(staging));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Debug, "05-transposition")));
        AssertPreserved(fixture.Debug, before, InputDependencies.PanelChain);
        Assert.True(restarted.IsCurrent(await Calculate(restarted, fixture.Settings)));
        Assert.False(File.Exists(Path.Combine(fixture.Debug, "05-transposition", "partial.xlsx")));
    }
    [Fact]
    public async Task StopDuringSunPathPublicationPreservesNumericalSolarData()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        await Calculate(service, fixture.Settings); var before = Artifacts(fixture.Debug);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Evaluate(fixture.Settings with { CameraTilt = 10 }, false,
            message => { if (message == "Drawing sun paths on the calibrated image") cts.Cancel(); }, null, cts.Token));
        AssertPreserved(fixture.Debug, before, InputDependencies.PoseChain);
        Assert.False(Directory.Exists(fixture.Debug + ".staging"));
        Assert.True(service.IsCurrent(await Calculate(service, fixture.Settings with { CameraTilt = 10 })));
    }
    private static string Legacy(Fixture fixture, string stage, string source)
    {
        string root = Path.Combine(fixture.Debug, "calibration-20260927-120000-000-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, stage); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Path.GetFileName(source)); File.Copy(source, path);
        AppData.Write(Path.Combine(root, "run.json"), new { Status = "Complete", StartedUtc = DateTimeOffset.UtcNow,
            Settings = fixture.Settings, Inputs = new { }, Stages = new Dictionary<string, object> {
                [stage] = new { Status = "Complete", Origin = "Fixture", LibraryVersion = Guid.NewGuid().ToString(), Artifacts = new[] { stage + "/" + Path.GetFileName(source) } } } });
        return path;
    }
    [Fact]
    public async Task MigrationPreservesSelectedProfileAndWeatherAndDoesNotTouchExportsOrExamples()
    {
        using var fixture = new Fixture();
        string profile = Legacy(fixture, "01-calibration", fixture.Settings.ProfilePath);
        string weather = Legacy(fixture, "03-irradiance", fixture.Settings.ImportPath);
        var settings = fixture.Settings with { ProfilePath = profile, ImportPath = weather };
        AppData.SaveSettings(settings);
        string export = Path.Combine(fixture.Debug, "scenario-user-export"), example = Path.Combine(fixture.Root, "Example", "Debug Data", "reference-run");
        Directory.CreateDirectory(export); Directory.CreateDirectory(example);
        File.WriteAllText(Path.Combine(export, "keep.txt"), "export"); File.WriteAllText(Path.Combine(example, "keep.txt"), "reference");
        using (var service = new AppServices(fixture.Debug))
        {
            settings = service.PrepareInputs(settings);
            Assert.True(File.Exists(profile)); Assert.True(File.Exists(weather));
            Assert.Equal(AppData.FileKey(fixture.Settings.ProfilePath), AppData.FileKey(PortablePaths.Resolve(settings.ProfilePath)));
            Assert.Equal(settings.ProfilePath, AppData.ReadSettings()!.ProfilePath);
            Assert.True(service.IsCurrent(await Calculate(service, settings)));
        }
        using var restarted = new AppServices(fixture.Debug);
        Assert.True(restarted.IsCurrent(await Calculate(restarted, AppData.ReadSettings()!)));
        Assert.Equal("export", File.ReadAllText(Path.Combine(export, "keep.txt"))); Assert.Equal("reference", File.ReadAllText(Path.Combine(example, "keep.txt")));
        string destination = Path.Combine(fixture.Root, "exports");
        AppServices.Export(await Calculate(restarted, settings), destination);
        Assert.False(Directory.Exists(Path.Combine(Assert.Single(Directory.GetDirectories(destination)), "scenario-user-export")));
    }
    [Fact]
    public void MigrationLeavesUnverifiedHistoryAndUnknownFilesAlone()
    {
        using var fixture = new Fixture(); string profile = Legacy(fixture, "01-calibration", fixture.Settings.ProfilePath);
        string history = Directory.GetParent(Path.GetDirectoryName(profile)!)!.FullName;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(profile)!, "user-notes.txt"), "keep");
        using var service = new AppServices(fixture.Debug); service.PrepareInputs(fixture.Settings);
        Assert.True(File.Exists(profile)); Assert.Equal("keep", File.ReadAllText(Path.Combine(Path.GetDirectoryName(profile)!, "user-notes.txt")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlyLegacyDiagnosticsDoNotBlockStartupAndSelectedInputsArePreserved(bool readOnlyDirectory) => Sta(() =>
    {
        using var fixture = new Fixture();
        string profile = Legacy(fixture, "01-calibration", fixture.Settings.ProfilePath);
        string protectedPath = readOnlyDirectory ? Path.GetDirectoryName(profile)! : profile;
        var originalAttributes = File.GetAttributes(protectedPath);
        string? preservedProfile = null;
        File.SetAttributes(protectedPath, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            using (var store = new DebugDataStore(fixture.Debug))
            {
                var migrated = store.CleanHistory(fixture.Settings with { ProfilePath = profile });
                preservedProfile = PortablePaths.Resolve(migrated.ProfilePath);
                Assert.Single(store.DeferredCleanup);
                Assert.True(File.Exists(profile));
                Assert.Equal(AppData.FileKey(profile), AppData.FileKey(PortablePaths.Resolve(migrated.ProfilePath)));
                store.PrepareInputs(migrated);
                Assert.Single(File.ReadAllLines(AppData.PathFor("maintenance.log")));
            }
            using var service = new AppServices(fixture.Debug);
            var window = new MainWindow(applicationServices: service, loadExampleOnFirstRun: false);
            try
            {
                Assert.Equal("Solar Irradiance", window.Workspaces.Active!.Title);
                Assert.NotNull(window.PvAutonomy);
                Assert.Null(window.Completed);
                Assert.True(File.Exists(profile));
            }
            finally { window.Close(); }
        }
        finally
        {
            if (File.Exists(protectedPath) || Directory.Exists(protectedPath)) File.SetAttributes(protectedPath, originalAttributes);
            // File.Copy preserves read-only attributes; restore the test copy before fixture disposal.
            if (preservedProfile != null && File.Exists(preservedProfile))
                File.SetAttributes(preservedProfile, File.GetAttributes(preservedProfile) & ~FileAttributes.ReadOnly);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void LockedLegacyFileDefersCleanupWithoutBlockingInputPreparation()
    {
        using var fixture = new Fixture(); string profile = Legacy(fixture, "01-calibration", fixture.Settings.ProfilePath);
        using var locked = new FileStream(profile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var store = new DebugDataStore(fixture.Debug);
        var settings = store.CleanHistory(fixture.Settings);
        Assert.Single(store.DeferredCleanup); Assert.True(File.Exists(profile));
        Assert.Equal(fixture.Settings, settings);
    }
    [Fact]
    public async Task SelectedCurrentProfileIsSavedBeforeInvalidationAndSurvivesUpdates()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        await Calculate(service, fixture.Settings);
        string profile = Path.Combine(fixture.Debug, "01-calibration", "camera-profile.json");
        var selected = service.PrepareInputs(fixture.Settings with { ProfilePath = profile });
        Assert.NotEqual(profile, PortablePaths.Resolve(selected.ProfilePath));
        await Calculate(service, selected with { CoverageAngle = 60 });
        Assert.True(File.Exists(PortablePaths.Resolve(selected.ProfilePath)));
    }
    [Fact]
    public void UnrecognizedStageFolderAndStagingFolderAreNeverDeleted()
    {
        using var fixture = new Fixture(); string stage = Path.Combine(fixture.Debug, "01-calibration"); Directory.CreateDirectory(stage);
        string file = Path.Combine(stage, "keep.txt"); File.WriteAllText(file, "user file");
        Assert.Throws<IOException>(() => new DebugDataStore(fixture.Debug)); Assert.Equal("user file", File.ReadAllText(file));
        string other = Path.Combine(fixture.Root, "Other Debug"), staging = other + ".staging"; Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "keep.txt"), "user staging");
        Assert.Throws<IOException>(() => new DebugDataStore(other)); Assert.True(File.Exists(Path.Combine(staging, "keep.txt")));
    }
    [Theory]
    [InlineData("Preparing sky mask")]
    [InlineData("Reading irradiance and interval metadata")]
    [InlineData("Preparing solar positions for source intervals")]
    [InlineData("Drawing sun paths on the calibrated image")]
    [InlineData("Calculating irradiance on the panel")]
    [InlineData("Applying panel shading and saving library results")]
    public async Task CancellationAtEachMajorStageLeavesNoStagingAndRetrySucceeds(string stopAt)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug); using var cts = new CancellationTokenSource();
        bool reached = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Evaluate(fixture.Settings, false,
            text => { if (text == stopAt) { reached = true; cts.Cancel(); } }, null, cts.Token));
        Assert.True(reached); Assert.False(Directory.Exists(fixture.Debug + ".staging"));
        Assert.NotEqual("Complete", AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))!.Status);
        Assert.True(service.IsCurrent(await Calculate(service, fixture.Settings)));
    }
    [Fact]
    public async Task FailedPublicationIsIncompleteAndRecoversAfterTheLockIsReleased()
    {
        using var fixture = new Fixture(); FileStream? locked = null;
        using (var service = new AppServices(fixture.Debug))
        {
            try
            {
                await Assert.ThrowsAnyAsync<IOException>(() => service.Evaluate(fixture.Settings, false, text =>
                {
                    if (text != "Calculating irradiance on the panel") return;
                    string folder = Path.Combine(fixture.Debug, "05-transposition"); Directory.CreateDirectory(folder);
                    locked = new FileStream(Path.Combine(folder, "panel-unshaded.xlsx"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                }, null, CancellationToken.None));
                Assert.NotEqual("Complete", AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))!.Status);
            }
            finally { locked?.Dispose(); }
            Assert.True(service.IsCurrent(await Calculate(service, fixture.Settings)));
            Assert.False(Directory.Exists(fixture.Debug + ".staging"));
        }
    }
    [Fact]
    public async Task InterruptedRefreshCannotReuseShadingFromThePreviousSourceSnapshot()
    {
        using var fixture = new Fixture(); int calls = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        { calls++; return SolarShade.ShadingCorrection.ShadingCorrectionModule.ApplyToPanel(input, scene, ct); });
        await Calculate(service, fixture.Settings); Assert.Equal(1, calls);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Evaluate(fixture.Settings, true,
            text => { if (text.StartsWith("Preparing solar positions")) cts.Cancel(); }, null, cts.Token));
        await Calculate(service, fixture.Settings); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task LeaseAlsoBlocksAnIndependentProcess()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        service.PrepareInputs(fixture.Settings);
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["SOLARSHADE_TEST_LOCK"] = fixture.Debug + ".lock";
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("try { $stream=[IO.File]::Open($env:SOLARSHADE_TEST_LOCK,'Open','ReadWrite','None'); $stream.Dispose(); exit 1 } catch [IO.IOException] { exit 0 }");
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
    [Fact]
    public async Task ClosingKeepsOwnershipUntilNativeWorkHasActuallyFinished()
    {
        using var fixture = new Fixture(); using var release = new ManualResetEventSlim(); int entered = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, _) =>
        {
            Interlocked.Increment(ref entered);
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
            return SolarShade.ShadingCorrection.ShadingCorrectionModule.ApplyToPanel(input, scene, CancellationToken.None);
        });
        var pending = Calculate(service, fixture.Settings);
        try
        {
            await Until(() => Volatile.Read(ref entered) == 1); service.Dispose();
            Assert.Throws<IOException>(() => new DebugDataStore(fixture.Debug));
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var reopened = new AppServices(fixture.Debug);
        Assert.True(reopened.IsCurrent(await Calculate(reopened, fixture.Settings)));
    }
}
