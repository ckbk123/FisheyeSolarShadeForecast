using System.IO;
using System.Windows.Threading;
using OpenCvSharp;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Desktop;
using SolarShade.Shading;
using SolarShade.SkyPhotoMasking;
using Xunit;

[CollectionDefinition("Manual update UI", DisableParallelization = true)]
public sealed class ManualUpdateCollection;

[Collection("Manual update UI")]
public sealed class ManualUpdateTests
{
    internal static void Sta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); } catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "UI test timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!ready()) { Assert.True(DateTime.UtcNow < deadline, "Update did not finish."); await Task.Delay(10); }
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "solar-manual-" + Guid.NewGuid().ToString("N"));
        public string Debug => Path.Combine(Root, "Debug Data");
        public UserSettings Settings { get; }
        private readonly string previousRoot = AppData.Root;
        public Fixture()
        {
            Directory.CreateDirectory(Root); AppData.Root = Path.Combine(Root, "Data");
            string imagePath = Path.Combine(Root, "photo.png");
            using var image = new Mat(303, 201, MatType.CV_8UC3, new Scalar(120, 85, 35));
            Cv2.ImWrite(imagePath, image);
            Settings = new() { SkyImage = imagePath, SkyFolder = Root, ProfilePath = Path.Combine(Root, "camera-profile.json"),
                ImportPath = Path.Combine(Root, "weather.csv"), Zone = "UTC", UseSystemTimeZone = false,
                Start = new(2025, 5, 15), End = new(2025, 5, 15), ImportWindow = 1, Substeps = 15 };
            var calibration = new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
                201, 303, [100, 151], [0, 70], 70, 95, null, 0, [], DateTimeOffset.UnixEpoch, "Synthetic test lens");
            CameraProfileFiles.Export(new(calibration, "Synthetic", "Test coverage"), Root);
            using var binary = new Mat(303, 201, MatType.CV_8UC1, new Scalar(255));
            Cv2.ImEncode(".png", binary, out var png);
            var mask = new SkyMaskResult(png, Settings.Model, 201, 303, Settings.Resolution,
                new(100, 151, 95, 201, 303), new(5, 56, 190, 190), "Synthetic", "Synthetic", null, 0, 0, 0, null, null);
            string key = AppData.Key(new { Image = AppData.FileKey(imagePath), Settings.Model, Settings.Resolution, Settings.CenteredDisk,
                Library = AppData.LibraryVersion(typeof(SkyPhotoMasker)), Package = AppData.LibraryVersion(typeof(Program)) });
            SkyMaskExporter.Export(mask, AppData.PathFor(Path.Combine("mask-stages", key)), "Synthetic fixture");
            WriteWeather(); AppData.SaveSettings(Settings);
        }
        public void WriteWeather() => File.WriteAllLines(Settings.ImportPath,
            new[] { "Timestamp,BHI,DHI" }.Concat(Enumerable.Range(0, 24).Select(hour => $"2025-05-15T{hour:00}:00:00+00:00,0,100")));
        public int Runs => AppData.Read<DebugDataRun.Manifest>(Path.Combine(Debug, "run.json"))?.UpdateNumber ?? 0;
        public void Dispose() { AppData.Root = previousRoot; Directory.Delete(Root, true); }
    }

    [Fact]
    public void EditsRequireExplicitUpdateAndDraftTextImmediatelyDisablesExport() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest);
            for (int i = 0; i < 100; i++) window.EditFieldForTest("PanelTilt", (i % 90).ToString());
            await Task.Delay(500);
            Assert.Equal(0, fixture.Runs); Assert.Null(window.Completed); Assert.Equal(0, service.CardinalGenerationCount);
            window.Calculate();
            Assert.Equal(UpdateState.Updating, window.UpdateStateForTest); Assert.False(window.ExportEnabledForTest);
            window.Calculate(); // A second click must not queue another run.
            await Until(() => !window.UpdatingForTest);
            Assert.Equal(1, fixture.Runs); Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest); Assert.True(window.ExportEnabledForTest);
            Assert.Equal(9, window.Completed!.Settings.PanelTilt);
            window.SetSunPathVisibleForTest(false); window.SetCardinalsVisibleForTest(false);
            Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest); Assert.True(window.ExportEnabledForTest);
            var first = window.Completed;
            window.EditFieldForTest("PanelTilt", "43");
            Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest); Assert.False(window.ExportEnabledForTest);
            await Task.Delay(500); Assert.Same(first, window.Completed); Assert.Equal(1, fixture.Runs);
            window.EditFieldForTest("Latitude", "invalid");
            Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest); Assert.False(window.UpdateEnabledForTest);
            window.Calculate(); Assert.Equal(1, fixture.Runs);
            window.EditFieldForTest("Latitude", "91"); Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest);
            window.EditFieldForTest("Latitude", "10.8"); Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest);
            window.Begin(true); await Until(() => !window.UpdatingForTest);
            Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest); Assert.Equal(43, window.Completed!.Settings.PanelTilt);
            Assert.Equal(fixture.Settings.ImportPath, window.Completed.Settings.ImportPath); Assert.Equal(2, fixture.Runs);
        }
        finally { window.Close(); }
    });

    [Theory][InlineData(false)][InlineData(true)]
    public void StopOrEditDuringUpdateRejectsLateCompletion(bool edit) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var release = new ManualResetEventSlim();
        int entered = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) =>
        {
            Interlocked.Increment(ref entered);
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test release not signaled.");
            return ShadingCorrectionModule.ApplyToPanel(input, scene, CancellationToken.None);
        });
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => Volatile.Read(ref entered) == 1);
            Assert.Equal(UpdateState.Updating, window.UpdateStateForTest); Assert.False(window.UpdateEnabledForTest);
            if (edit) window.EditFieldForTest("PanelTilt", "42"); else window.StopUpdate();
            Assert.Equal(edit ? UpdateState.UpdateRequired : UpdateState.Stopped, window.UpdateStateForTest);
            window.Calculate(); Assert.Equal(1, Volatile.Read(ref entered));
            release.Set(); await Until(() => !window.UpdatingForTest);
            Assert.Null(window.Completed); Assert.False(window.ExportEnabledForTest); Assert.True(window.UpdateEnabledForTest);
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest); Assert.Equal(2, fixture.Runs);
        }
        finally { release.Set(); window.Close(); }
    });

    [Fact]
    public void FailureIsRedAndRetryCanBecomeGreen() => Sta(async () =>
    {
        using var fixture = new Fixture(); File.WriteAllText(fixture.Settings.ImportPath, "invalid workbook");
        using var service = new AppServices(fixture.Debug); var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.Equal(UpdateState.Failed, window.UpdateStateForTest); Assert.False(window.ExportEnabledForTest); Assert.True(window.UpdateEnabledForTest);
            fixture.WriteWeather(); window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest); Assert.True(window.ExportEnabledForTest);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void IncompleteShadedResultCannotTurnGreen() => Sta(async () =>
    {
        using var fixture = new Fixture();
        using var service = new AppServices(fixture.Debug, (input, _, _) => new(
            input.Rows.Select(r => new PanelRow(r.Interval.Timestamp, r.Interval.Start, r.Interval.End, 0, 100, null, null, null)
                { IntervalId = r.Interval.Id, SourceStart = r.Interval.SourceStart, SourceEnd = r.Interval.SourceEnd }).ToArray(), null, 0, "Incomplete fixture"));
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.NotNull(window.Completed); Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest);
            Assert.False(window.ExportEnabledForTest);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SystemTimeZoneChangesInvalidateWithoutCalculating() => Sta(async () =>
    {
        using var fixture = new Fixture();
        AppData.SaveSettings(fixture.Settings with { UseSystemTimeZone = true });
        var systemZone = TimeZoneInfo.Utc;
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(() => systemZone, applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            var first = window.Completed; Assert.Equal(UpdateState.UpToDate, window.UpdateStateForTest);
            systemZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            window.RefreshSystemTimeZone(); await Task.Delay(500);
            Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest); Assert.False(window.ExportEnabledForTest);
            Assert.Same(first, window.Completed); Assert.Equal(1, fixture.Runs);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MissingInputsAndLoadingExampleNeverStartWork() => Sta(async () =>
    {
        using var fixture = new Fixture(); File.Delete(fixture.Settings.ProfilePath);
        using var service = new AppServices(fixture.Debug); var window = new MainWindow(applicationServices: service);
        try
        {
            Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest); Assert.False(window.UpdateEnabledForTest);
            window.Calculate(); Assert.Equal(0, fixture.Runs);
            window.EditFieldForTest("Latitude", "invalid");
            await window.LoadExample(); await Task.Delay(500);
            Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest);
            Assert.Equal(0, fixture.Runs); Assert.Null(window.Completed); Assert.Equal(0, service.CardinalGenerationCount);
        }
        finally { window.Close(); }
    });
}
