using System.IO;
using System.Windows.Controls;
using SolarShade.Desktop;
using SolarShade.ShadingCorrection;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class WorkspaceTests
{
    private sealed class TestView : UserControl, IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }
    [Fact]
    public void HostKeepsInstancesAndUnavailableActivePageCanExplainItsState() => Sta(() =>
    {
        var host = new WorkspaceHost(); var a = new TestView(); var b = new TestView();
        host.Register(new("a", "Solar Irradiance", a));
        host.Register(new("b", "Future workspace", b));
        for (int i = 0; i < 20; i++) { Assert.True(host.Select("b")); Assert.Same(b, host.Active!.View); host.Select("a"); }
        host.SetAvailability("b", false, "Source required"); Assert.False(host.Select("b"));
        host.SetAvailability("b", true, null); Assert.True(host.Select("b"));
        host.SetAvailability("b", false, "Source changed"); Assert.Same(b, host.Active!.View);
        host.Select("a"); Assert.False(host.Select("b"));
        host.Dispose(); host.Dispose(); Assert.Equal(1, a.Disposals); Assert.Equal(1, b.Disposals);
        return Task.CompletedTask;
    });

    [Fact]
    public void SourceSnapshotIsFrozenAndRelevantEditsInvalidateWithoutNavigationWork() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            Assert.Equal("Solar Irradiance", window.Workspaces.Active!.Title);
            Assert.False(window.IrradianceController.Source.IsReady);
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            var source = window.IrradianceController.Source.Snapshot!;
            Assert.NotNull(source); Assert.True(window.IrradianceController.IsCurrent(source));
            var settings = source.Settings; settings.PanelTilt = 89;
            Assert.NotEqual(89, source.Settings.PanelTilt);
            window.Workspaces.Register(new("test", "Test dependent", new TestView()));
            int count = service.PreparationCount;
            window.Workspaces.Select("test"); window.Workspaces.Select("irradiance");
            Assert.Same(source, window.IrradianceController.Source.Snapshot);
            Assert.Equal(count, service.PreparationCount);
            window.EditFieldForTest("PanelTilt", "40");
            Assert.False(window.IrradianceController.Source.IsReady);
            Assert.False(window.IrradianceController.IsCurrent(source));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ReadinessRejectsContiguousTruncatedAndInvalidFinalCurvesButAcceptsNightZeros()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var settings = new UserSettings { Start = start.Date, End = start.Date, Zone = "UTC" };
        var rows = Enumerable.Range(0, 24).Select(i => new PanelRow(start.AddHours(i), start.AddHours(i), start.AddHours(i + 1),
            0, 0, 0, 0, 0) { IntervalId = i.ToString() }).ToArray();
        IrradianceReadiness.ValidatePanel(new(rows, 1, 0, "test"), settings);
        Assert.Throws<InvalidDataException>(() => IrradianceReadiness.ValidatePanel(new(rows.Skip(1).ToArray(), 1, 0, "test"), settings));
        rows[8] = rows[8] with { AfterDirect = double.NaN };
        Assert.Throws<InvalidDataException>(() => IrradianceReadiness.ValidatePanel(new(rows, 1, 0, "test"), settings));
    }

    [Fact]
    public async Task OptionalPublicationDoesNotChangeUpstreamManifestOrExport()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var evaluation = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        var source = IrradianceReadiness.Inspect(service, evaluation).Snapshot!;
        string manifest = Path.Combine(fixture.Debug, "run.json"), before = AppData.FileKey(manifest);
        var optional = service.CreateDependentStore("07-test", "test-v1", "result.json");
        optional.Publish(source, "settings-a", dir => File.WriteAllText(Path.Combine(dir, "result.json"), "{}"));
        Assert.True(optional.IsCurrent(source, "settings-a"));
        Assert.False(optional.IsCurrent(source, "settings-b"));
        Assert.Equal(before, AppData.FileKey(manifest)); Assert.True(evaluation.ManagedDebugRun!.HasCompleteDataset);
        string export = AppServices.Export(evaluation, Path.Combine(fixture.Root, "exports"));
        Assert.False(Directory.Exists(Path.Combine(export, "07-test")));
        string dependentExport = optional.Export(source, "settings-a", Path.Combine(fixture.Root, "exports"));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(dependentExport, "result.json")));
        File.WriteAllText(Path.Combine(optional.DirectoryPath, "result.json"), "changed");
        Assert.False(optional.IsCurrent(source, "settings-a"));
        Assert.Throws<IOException>(() => optional.Export(source, "settings-a", Path.Combine(fixture.Root, "exports")));
        Assert.True(evaluation.ManagedDebugRun.HasCompleteDataset);
        optional.Invalidate(); Assert.False(Directory.Exists(optional.DirectoryPath));
        Assert.Equal(before, AppData.FileKey(manifest));
    }

    [Fact]
    public async Task FailedOrObsoleteDependentWriteNeverPublishesCurrentResult()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var evaluation = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        var source = IrradianceReadiness.Inspect(service, evaluation).Snapshot!;
        var optional = service.CreateDependentStore("07-test", "test-v1", "result.json");
        Assert.Throws<IOException>(() => optional.Publish(source, "a", dir => throw new IOException("Injected failure")));
        Assert.False(Directory.Exists(optional.DirectoryPath)); Assert.False(Directory.Exists(optional.DirectoryPath + ".pending"));
        Assert.True(evaluation.ManagedDebugRun!.HasCompleteDataset);
        Assert.Throws<InvalidOperationException>(() => optional.Publish(source, "a", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "result.json"), "{}");
            service.ObserveInputs(fixture.Settings with { PanelTilt = 40 });
        }));
        Assert.False(Directory.Exists(optional.DirectoryPath)); Assert.False(Directory.Exists(optional.DirectoryPath + ".pending"));
    }

    [Fact]
    public async Task OptionalStorePreservesUnrecognizedFiles()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var evaluation = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        var source = IrradianceReadiness.Inspect(service, evaluation).Snapshot!;
        var optional = service.CreateDependentStore("07-test", "test-v1", "result.json");
        Directory.CreateDirectory(optional.DirectoryPath);
        File.WriteAllText(Path.Combine(optional.DirectoryPath, "user.txt"), "keep");
        Assert.Throws<IOException>(() => optional.Publish(source, "a", dir => File.WriteAllText(Path.Combine(dir, "result.json"), "{}")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(optional.DirectoryPath, "user.txt")));
        Assert.True(evaluation.ManagedDebugRun!.HasCompleteDataset);
    }
}
