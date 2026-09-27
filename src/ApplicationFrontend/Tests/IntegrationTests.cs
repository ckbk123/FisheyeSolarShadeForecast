using System.Globalization;
using System.IO;
using System.Text.Json;
using SolarShade.Desktop;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Shading;
using SolarShade.ShadingCorrection;
using Xunit;

public sealed class IntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "solar-orchestration-" + Guid.NewGuid().ToString("N"));
    private UserSettings Input(int minutes = 60)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "weather.csv");
        var start = new DateTimeOffset(2025, 5, 15, 0, 0, 0, TimeSpan.Zero);
        File.WriteAllLines(path, new[] { "Timestamp,BHI,DHI" }.Concat(Enumerable.Range(0, 1440 / minutes).Select(i => $"{start.AddMinutes(i * minutes):O},0,100.123456789")));
        return new() { ImportPath = path, ImportWindow = 1, ImportIntervalMinutes = minutes, Start = start.Date, End = start.Date, Zone = "UTC", PanelTilt = 0, Substeps = 15 };
    }
    private string Debug => Path.Combine(root, "Debug Data");

    [Theory][InlineData(60,24)][InlineData(15,96)][InlineData(30,48)]
    public async Task FrontendUsesTheSameLibraryResultsAndNativeCadence(int minutes, int count)
    {
        var settings = Input(minutes);
        using var service = new AppServices(Debug);
        var actual = await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        var dataset = IrradianceDatasetFiles.Import(settings.ImportPath, TimeZoneInfo.Utc, TimeSpan.FromMinutes(minutes), TimestampLabel.Start);
        var selected = IrradianceDatasets.SelectCalendarRange(dataset, settings.Start, settings.End, TimeZoneInfo.Utc);
        var solar = new SolarPositionModule(new(settings.Latitude, settings.Longitude, settings.Elevation)).PrepareIntervals(selected, settings.Substeps);
        var transposed = TranspositionModule.ComputePrepared(solar, new(settings.PanelTilt,settings.PanelAzimuth));
        var expected = ShadingCorrectionModule.ApplyToPanel(transposed, null);
        Assert.Equal(count, actual.Run.Rows.Count);
        Assert.Equal(expected.Rows, actual.Run.Rows);
        Assert.Equal(expected.BeforeEnergy, actual.Run.BeforeEnergy);
        Assert.Null(actual.Run.AfterEnergy);
        Assert.Null(actual.SunPath);
        Assert.Equal(0, actual.SunPathGenerationCount);
        Assert.True(File.Exists(Path.Combine(actual.DebugDirectory!, "03-irradiance", "horizontal-irradiance.xlsx")));
        Assert.True(File.Exists(Path.Combine(actual.DebugDirectory!, "04-solar-positions", "solar-positions.xlsx")));
        Assert.True(File.Exists(Path.Combine(actual.DebugDirectory!, "05-transposition", "panel-unshaded.xlsx")));
        Assert.False(File.Exists(Path.Combine(actual.DebugDirectory!, "06-shading", "panel-shaded.xlsx")));
        using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(actual.DebugDirectory!, "run.json")));
        Assert.Equal("Complete", status.RootElement.GetProperty("Status").GetString());
        Assert.Equal("Skipped", status.RootElement.GetProperty("Stages").GetProperty("06-shading").GetProperty("Status").GetString());
    }

    [Fact]
    public async Task PanelEditReusesSolarButChangedSourceInvalidatesIt()
    {
        var settings = Input(15); using var service = new AppServices(Debug);
        var first = await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        var second = await service.Evaluate(settings with { PanelTilt = 60 }, false, _ => { }, null, CancellationToken.None);
        Assert.Equal(first.PreparationCount, second.PreparationCount);
        Assert.NotEqual(first.Run.BeforeEnergy, second.Run.BeforeEnergy);
        Assert.NotEqual(first.DebugDirectory, second.DebugDirectory);
        Assert.Contains("Reused cached solar timeline", File.ReadAllText(Path.Combine(second.DebugDirectory!, "run.json")));
        var originalTime = File.GetLastWriteTimeUtc(settings.ImportPath);
        File.WriteAllText(settings.ImportPath, File.ReadAllText(settings.ImportPath).Replace("100.123456789", "200.123456789"));
        File.SetLastWriteTimeUtc(settings.ImportPath, originalTime);
        var third = await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        Assert.Equal(second.PreparationCount + 1, third.PreparationCount);
        Assert.NotEqual(first.Run.BeforeEnergy, third.Run.BeforeEnergy);
    }

    [Fact]
    public async Task LibraryReturnedSentinelIsNotRecalculatedByFrontendOrExport()
    {
        var settings = Input();
        PanelRun? sentinel = null; int calls = 0;
        using var service = new AppServices(Debug, (input, _, ct) =>
        {
            calls++;
            sentinel = new(input.Rows.Select(r => new PanelRow(r.Interval.Timestamp,r.Interval.Start,r.Interval.End,432.123456789,17,null,null,null)
                { IntervalId=r.Interval.Id,SourceStart=r.Interval.SourceStart,SourceEnd=r.Interval.SourceEnd }).ToArray(), null,0,"Sentinel");
            return sentinel;
        });
        var actual = await service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);
        Assert.Equal(1,calls); Assert.Same(sentinel,actual.Run);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(actual.DebugDirectory!, "06-shading", "panel-results.json")));
        Assert.Equal(sentinel!.BeforeEnergy,saved.RootElement.GetProperty("BeforeEnergy").GetDouble());
        AppServices.Export(actual,Path.Combine(root,"exports"));
        string copy = Assert.Single(Directory.GetDirectories(Path.Combine(root,"exports")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(actual.DebugDirectory!, "06-shading", "panel-results.json")), File.ReadAllBytes(Path.Combine(copy,"06-shading","panel-results.json")));
    }

    [Fact]
    public async Task NativeWorkbookMetadataOverridesLegacyHourlyFallback()
    {
        var settings=Input(15);
        var data=IrradianceDatasetFiles.Import(settings.ImportPath,TimeZoneInfo.Utc,TimeSpan.FromMinutes(15),TimestampLabel.Start);
        string workbook=Path.Combine(root,"native.xlsx"); IrradianceDatasetFiles.Export(data,workbook);
        using var service=new AppServices(Debug);
        var result=await service.Evaluate(settings with { ImportPath=workbook,ImportIntervalMinutes=60 },false,_=>{},null,CancellationToken.None);
        Assert.Equal(96,result.Run.Rows.Count);
        Assert.All(result.Run.Rows,row=>Assert.Equal(TimeSpan.FromMinutes(15),row.End-row.Start));
    }

    [Fact]
    public async Task ExplicitVariableIntervalsStayUnchangedThroughThePipeline()
    {
        var settings = Input();
        var start = new DateTimeOffset(settings.Start, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 24).SelectMany(hour => new[]
        {
            new IrradianceInterval($"{hour}-short", start.AddHours(hour), start.AddHours(hour), start.AddHours(hour).AddMinutes(10), start.AddHours(hour), start.AddHours(hour).AddMinutes(10), 0, 100),
            new IrradianceInterval($"{hour}-long", start.AddHours(hour).AddMinutes(10), start.AddHours(hour).AddMinutes(10), start.AddHours(hour + 1), start.AddHours(hour).AddMinutes(10), start.AddHours(hour + 1), 0, 200)
        }).ToArray();
        var data = new IrradianceDataset(rows, "Variable source", "UTC", "Explicit intervals", TimestampLabel.Explicit, null);
        string workbook = Path.Combine(root, "variable.xlsx");
        IrradianceDatasetFiles.Export(data, workbook);
        using var service = new AppServices(Debug);
        var result = await service.Evaluate(settings with { ImportPath = workbook }, false, _ => { }, null, CancellationToken.None);
        Assert.Null(result.Raw.NativeCadence);
        Assert.Equal(rows.Select(r => (r.Id, r.Timestamp, r.Start, r.End)), result.Run.Rows.Select(r => (r.IntervalId, r.Timestamp, r.Start, r.End)));
        Assert.Equal(4.4, result.Run.BeforeEnergy, 10);
    }

    [Fact]
    public async Task CancellationRetainsEarlierStagesAndDoesNotPoisonCache()
    {
        var settings=Input(); using var service=new AppServices(Debug); using var cts=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.Evaluate(settings,false,
            message=> { if(message.StartsWith("Preparing solar positions"))cts.Cancel(); },null,cts.Token));
        string cancelled=Assert.Single(Directory.GetDirectories(Debug));
        using var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(cancelled,"run.json")));
        Assert.Equal("Cancelled",state.RootElement.GetProperty("Status").GetString());
        Assert.True(File.Exists(Path.Combine(cancelled,"03-irradiance","horizontal-irradiance.xlsx")));
        var next=await service.Evaluate(settings,false,_=>{},null,CancellationToken.None);
        Assert.Equal(24,next.Run.Rows.Count);
        Assert.NotEqual(cancelled,next.DebugDirectory);
    }

    [Fact]
    public async Task MissingIntervalFailsWithoutInventingLongerSourceIntervals()
    {
        var settings=Input(15); File.WriteAllLines(settings.ImportPath,File.ReadAllLines(settings.ImportPath).Where((_,i)=>i!=7).ToArray());
        using var service=new AppServices(Debug);
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.Evaluate(settings,false,_=>{},null,CancellationToken.None));
        string run=Assert.Single(Directory.GetDirectories(Debug));
        using var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(run,"run.json")));
        Assert.Equal("Failed",state.RootElement.GetProperty("Status").GetString());
        Assert.False(File.Exists(Path.Combine(run,"05-transposition","panel-unshaded.xlsx")));
    }
    public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
}
