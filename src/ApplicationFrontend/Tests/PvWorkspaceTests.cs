using System.IO;
using SolarShade.Desktop;
using SolarShade.PvBattery;
using SolarShade.PvBattery.Integration;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class PvWorkspaceTests
{
    [Fact]
    public void SettingsConvertPercentagesOnceAndFreezeLoad()
    {
        var draft = PvSettingsDraft.Example(); var settings = draft.Parse();
        Assert.Equal(.2, settings.PanelEfficiency); Assert.Equal(.9, settings.ConversionEfficiency); Assert.Equal(.5, settings.InitialSoc);
        Assert.Equal(2, settings.PanelAreaM2); Assert.Equal(1000, settings.BatteryCapacityWh);
        draft.Load[0] = "999"; Assert.Equal(20, settings.HourlyLoadWh[0]);
        Assert.Equal(2.5, (draft with { Area = "2,5" }).Parse().PanelAreaM2);
    }
    [Theory]
    [InlineData("", "20", "90", "1000", "50")]
    [InlineData("NaN", "20", "90", "1000", "50")]
    [InlineData("-1", "20", "90", "1000", "50")]
    [InlineData("2", "0", "90", "1000", "50")]
    [InlineData("2", "101", "90", "1000", "50")]
    [InlineData("2", "20", "0", "1000", "50")]
    [InlineData("2", "20", "90", "0", "50")]
    [InlineData("2", "20", "90", "1000", "101")]
    public void InvalidScalarDraftsBlockEvaluation(string area, string panel, string conversion, string capacity, string soc)
    { Assert.Throws<ArgumentException>(() => (PvSettingsDraft.Example() with { Area = area, PanelEfficiency = panel, ConversionEfficiency = conversion, Capacity = capacity, InitialSoc = soc }).Parse()); }

    [Fact]
    public void PasteIsAtomicAndBlankSlotsNeverBecomeZero() => Sta(() =>
    {
        var editor = new DailyLoadEditor(); var before = PvSettingsDraft.Example().Load; editor.SetValues(before);
        Assert.False(editor.TryPaste("1\t2")); Assert.Equal(before, editor.Values);
        var bad = Enumerable.Repeat("1", 24).ToArray(); bad[8] = "";
        Assert.False(editor.TryPaste(string.Join('\t', bad))); Assert.Equal(before, editor.Values);
        bad[8] = "-1"; Assert.False(editor.TryPaste(string.Join('\n', bad))); Assert.Equal(before, editor.Values);
        Assert.True(editor.TryPaste(string.Join("\r\n", Enumerable.Repeat("0", 24)) + "\r\n")); Assert.All(editor.Values, v => Assert.Equal("0", v));
        return Task.CompletedTask;
    });

    [Fact]
    public void FullWorkspaceUsesBackendAndBatteryEditsLeaveSourceUntouched() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            Assert.False(window.Workspaces.Select("pv"));
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.True(window.Workspaces.Select("pv"));
            var pv = window.PvAutonomy.Controller; pv.SetDraft(PvSettingsDraft.Example());
            string hash = AppData.FileKey(Path.Combine(fixture.Debug, "run.json")); int count = service.PreparationCount;
            await pv.EvaluateAsync(); Assert.True(pv.IsCurrent, pv.Status);
            var expected = PanelBatterySimulation.Compute(window.Completed!.Run, pv.Draft.Parse(), window.Completed.Settings.Zone);
            Assert.Equal(expected.Hours, pv.Result!.Hours); Assert.Equal(expected.Summary, pv.Result.Summary);
            string? exported = await pv.ExportAsync(Path.Combine(fixture.Root, "exports")); Assert.NotNull(exported);
            foreach (string name in new[] { "battery-hourly.csv", "battery-hourly.xlsx", "battery-result.json" })
                Assert.Equal(AppData.FileKey(Path.Combine(fixture.Debug, "07-battery", name)), AppData.FileKey(Path.Combine(exported!, name)));
            window.PvAutonomy.Chart.Day(fixture.Settings.Start); window.Workspaces.Select("irradiance"); window.Workspaces.Select("pv");
            Assert.True(pv.IsCurrent); Assert.Equal(count, service.PreparationCount);
            pv.SetDraft(pv.Draft with { Capacity = "" }); Assert.False(pv.CanEvaluate); Assert.False(pv.CanExport);
            Assert.Equal(hash, AppData.FileKey(Path.Combine(fixture.Debug, "run.json"))); Assert.True(window.ExportEnabledForTest);
            Assert.Equal(count, service.PreparationCount);
            pv.SetDraft(PvSettingsDraft.Example()); await pv.EvaluateAsync(); Assert.True(pv.IsCurrent, pv.Status);
            window.EditFieldForTest("PanelTilt", "not a number"); Assert.False(pv.CanEvaluate); Assert.False(pv.CanExport);
            Assert.Equal("pv", window.Workspaces.Active!.Id); window.Workspaces.Select("irradiance"); Assert.False(window.Workspaces.Select("pv"));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("edit")]
    [InlineData("stop")]
    [InlineData("source")]
    [InlineData("dispose")]
    public async Task LateBackendCompletionNeverPublishesAfterInvalidation(string action)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var evaluation = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        using var source = new IrradianceWorkspaceController(service) { Completed = evaluation, State = UpdateState.UpToDate };
        source.RefreshSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); int calls = 0;
        var backend = new PvEvaluationService(source, (s, settings, _) =>
        { Interlocked.Increment(ref calls); started.SetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return PanelBatterySimulation.Compute(s.Run, settings, s.TimeZoneId); });
        using var pv = new PvWorkspaceController(source, backend); pv.SetDraft(PvSettingsDraft.Example());
        var run = pv.EvaluateAsync(); await started.Task;
        await pv.EvaluateAsync(); Assert.Equal(1, calls); Assert.True(source.DependentBusy);
        if (action == "edit") pv.SetDraft(pv.Draft with { Capacity = "2000" });
        if (action == "stop") pv.Stop();
        if (action == "source") { source.State = UpdateState.UpdateRequired; source.RefreshSource(); }
        if (action == "dispose") pv.Dispose();
        release.Set(); await run;
        Assert.False(pv.IsCurrent); Assert.False(source.DependentBusy); Assert.Null(pv.Result);
        Assert.False(Directory.Exists(backend.Store.DirectoryPath)); Assert.True(evaluation.ManagedDebugRun!.HasCompleteDataset);
    }

    [Fact]
    public async Task DamagedPvArtifactsBlockOnlyPvExportAndDraftsRestoreWithoutResults()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var evaluation = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        using var source = new IrradianceWorkspaceController(service) { Completed = evaluation, State = UpdateState.UpToDate }; source.RefreshSource();
        var backend = new PvEvaluationService(source);
        using (var pv = new PvWorkspaceController(source, backend))
        {
            pv.SetDraft(PvSettingsDraft.Example()); await pv.EvaluateAsync(); Assert.True(pv.IsCurrent, pv.Status);
            File.AppendAllText(Path.Combine(backend.Store.DirectoryPath, "battery-hourly.csv"), "tamper");
            Assert.Null(await pv.ExportAsync(Path.Combine(fixture.Root, "exports"))); Assert.False(pv.IsCurrent);
            Assert.True(evaluation.ManagedDebugRun!.HasCompleteDataset);
            pv.SetDraft(pv.Draft with { Capacity = "unfinished" });
        }
        using var restored = new PvWorkspaceController(source, backend);
        Assert.Equal("unfinished", restored.Draft.Capacity); Assert.Null(restored.Result); Assert.False(restored.CanExport);
    }

    [Fact]
    public async Task InPlaceSourceChangeDuringOptionalWriteIsRejectedWithoutObserverCallback()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var evaluation = await service.Evaluate(fixture.Settings, false, _ => { }, null, CancellationToken.None);
        var source = IrradianceReadiness.Inspect(service, evaluation).Snapshot!;
        var store = service.CreateDependentStore("07-test", "v1", "result.json");
        Assert.Throws<InvalidOperationException>(() => store.Publish(source, "key", folder =>
        { File.WriteAllText(Path.Combine(folder, "result.json"), "{}"); File.AppendAllText(fixture.Settings.ImportPath, "\n"); }));
        Assert.False(Directory.Exists(store.DirectoryPath));
    }
}
