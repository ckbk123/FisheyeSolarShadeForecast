using System.IO;
using System.Diagnostics;
using SolarShade.Desktop;
using SolarShade.Irradiance;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public class PerformanceFixTests
{
    [Fact]
    public void CacheRejectsIdentityBeforeAllocatingPayloadAndDetectsCorruption()
    {
        using var fixture = new Fixture(); var cache = new CurrentValueCache(fixture.Debug);
        cache.Write("test", "correct", new string('x', 8_000_000));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(cache.Read<string>("test", "different"));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 100_000);
        Assert.Equal(8_000_000, cache.Read<string>("test", "correct")!.Length);
        string path = cache.PathFor("test");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        { stream.Position = stream.Length - 12; int value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 1)); }
        Assert.Null(cache.Read<string>("test", "correct"));
        File.WriteAllBytes(path, [1, 2]); Assert.Null(cache.Read<string>("test", "correct"));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void SolarCachePreservesClippedSamplesAndSharesEqualSamples(bool clipped)
    {
        using var fixture = new Fixture(); var cache = new CurrentValueCache(fixture.Debug);
        var start = DateTimeOffset.UnixEpoch;
        var interval = new IrradianceInterval("a", start, start, start.AddHours(1), clipped ? start.AddMinutes(30) : start, start.AddHours(1), 1, 2);
        SolarGeometrySample[] full = [new(start.AddMinutes(15), 20, 21, 22, .5), new(start.AddMinutes(45), 30, 31, 32, .5)];
        SolarGeometrySample[] selected = clipped ? [new(start.AddMinutes(45), 30, 31, 32, 1)] : full;
        var dataset = new IrradianceDataset([interval], "fixture", "UTC", "start", TimestampLabel.Start, TimeSpan.FromHours(1));
        var solar = new SolarTimeline(dataset, [new(interval, full[0], full, selected)], "fixture");
        cache.Write("solar", "key", solar); var loaded = cache.Read<SolarTimeline>("solar", "key")!;
        Assert.Equal(solar.Dataset.FetchedUtc, loaded.Dataset.FetchedUtc);
        Assert.Equal(full, loaded.Intervals[0].SourceSamples); Assert.Equal(selected, loaded.Intervals[0].Samples);
        if (!clipped) Assert.Same(loaded.Intervals[0].SourceSamples, loaded.Intervals[0].Samples);
    }
    [Fact]
    public async Task VerifiedUnchangedUpdateDoesNotRepublishButTamperingForcesRepair()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var first = await service.Evaluate(fixture.Settings, false, _ => { }, null, default);
        var manifest = File.ReadAllBytes(Path.Combine(fixture.Debug, "run.json"));
        var second = await service.Evaluate(fixture.Settings, false, _ => { }, null, default);
        Assert.Same(first.Run, second.Run); Assert.Equal(1, fixture.Runs);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(fixture.Debug, "run.json")));
        File.AppendAllText(Path.Combine(fixture.Debug, "05-transposition", "panel-unshaded.xlsx"), "tamper");
        var repaired = await service.Evaluate(fixture.Settings, false, _ => { }, null, default);
        Assert.Equal(2, fixture.Runs); Assert.True(service.IsCurrent(repaired));
    }
    [Fact]
    public void EditingDoesNotWaitForStorageAndInvalidatesImmediately() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(applicationServices: service);
        try
        {
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var blocker = Task.Run(() => { lock (window.Completed!.ManagedDebugRun!.SyncRoot) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            try
            {
                var timer = Stopwatch.StartNew(); window.EditFieldForTest("PanelTilt", "44");
                Assert.False(window.ExportEnabledForTest); Assert.Equal(UpdateState.UpdateRequired, window.UpdateStateForTest);
                Assert.True(timer.ElapsedMilliseconds < 200, "An edit waited for diagnostic storage.");
            }
            finally { release.Set(); }
            await blocker; await window.InputsSettled;
        }
        finally { await window.InputsSettled; window.Close(); }
    });
    [Fact]
    public void DenseChartPreservesPeaksTroughsAndGaps()
    {
        var start = DateTimeOffset.UnixEpoch;
        var rows = Enumerable.Range(0, 10000).Select(i => new PanelRow(start.AddHours(i + 1), start.AddHours(i), start.AddHours(i + 1), i == 4321 ? 1000 : i == 4322 ? -10 : 10, 0, i == 5000 ? null : 1, 0, null)).ToArray();
        var before = ChartSteps.Envelopes(rows, 0, rows.Length - 1, r => r.BeforeTotal, 100).ToArray();
        Assert.Contains(before, v => v.Maximum == 1000); Assert.Contains(before, v => v.Minimum == -10);
        Assert.Equal(start, before[0].Start); Assert.Equal(start.AddHours(10000), before[^1].End);
        Assert.True(before.Length <= 100);
        var after = ChartSteps.Envelopes(rows, 0, rows.Length - 1, r => r.AfterTotal, 100).ToArray();
        Assert.Equal(2, after.Count(v => v.StartFigure));
    }
}
