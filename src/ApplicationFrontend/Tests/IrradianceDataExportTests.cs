using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using SolarShade.Desktop;
using SolarShade.Irradiance;
using SolarShade.ShadingCorrection;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class IrradianceDataExportTests
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static Task<Evaluation> Calculate(AppServices service, UserSettings settings) =>
        service.Evaluate(settings, false, _ => { }, null, CancellationToken.None);

    private static XDocument Part(ZipArchive workbook, string path)
    {
        using var stream = workbook.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }
    private static string? Cell(XElement row, char column) => row.Elements(Spreadsheet + "c")
        .SingleOrDefault(c => ((string?)c.Attribute("r"))?.StartsWith(column) == true) is { } cell
            ? cell.Element(Spreadsheet + "v")?.Value ?? cell.Descendants(Spreadsheet + "t").Single().Value : null;
    private static double? Number(XElement row, char column) => Cell(row, column) is { } value
        ? double.Parse(value, CultureInfo.InvariantCulture) : null;
    private static void NoPublishedExport(string root)
    {
        Assert.False(Directory.Exists(Path.Combine(root, "export")));
        Assert.Empty(Directory.GetDirectories(root, ".solarshade-export-*"));
    }

    [Theory]
    [InlineData(false, 60, "Hay–Davies")]
    [InlineData(true, 15, "Isotropic")]
    public async Task WorkbookKeepsAllSixColumnsNativeIntervalsAndSavedValues(bool isotropic, int cadence, string model)
    {
        using var fixture = new Fixture();
        var settings = fixture.Settings with { Isotropic = isotropic, ImportIntervalMinutes = cadence, Zone = "SE Asia Standard Time" };
        var start = new DateTimeOffset(2025, 5, 15, 0, 0, 0, TimeSpan.FromHours(7));
        File.WriteAllLines(settings.ImportPath, new[] { "Timestamp,BHI,DHI" }.Concat(
            Enumerable.Range(0, 24 * 60 / cadence).Select(i => $"{start.AddMinutes(i * cadence):O},0,100")));
        int calculations = 0;
        using var service = new AppServices(fixture.Debug, (input, scene, ct) => {
            calculations++;
            var actual = ShadingCorrectionModule.ApplyToPanel(input, scene, ct);
            return actual with { Rows = actual.Rows.Select((row, i) => row with {
                BeforeDirect = i == 0 ? 0 : 200.1234567890123,
                BeforeDiffuse = i == 0 ? 0 : 80.9876543210987,
                AfterDirect = i == 0 ? 0 : 50.030864197253075,
                AfterDiffuse = i == 0 ? 0 : 60.740740740824025
            }).ToArray() };
        });
        var result = await Calculate(service, settings);
        var expected = result.Run.Rows.ToArray();
        // Export authority is the saved successful update, including its model, not these public objects.
        result.Settings.Isotropic = !isotropic;
        var modified = result with { Run = result.Run with { Model = "untrusted", Rows = [] } };
        string parent = Path.Combine(fixture.Root, "Dữ liệu xuất"); Directory.CreateDirectory(parent);
        string folder = AppServices.Export(modified, parent, new DataExportOptions());
        Assert.Equal(parent, Path.GetDirectoryName(folder));
        Assert.Equal(1, calculations);
        Assert.True(service.IsCurrent(result));
        Assert.Equal(new[] { "export.json", "irradiance.xlsx" }, Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray());
        Assert.Empty(Directory.GetDirectories(folder));
        using var workbook = ZipFile.OpenRead(Path.Combine(folder, "irradiance.xlsx"));
        Assert.Single(Part(workbook, "xl/workbook.xml").Descendants(Spreadsheet + "sheet"));
        var rows = Part(workbook, "xl/worksheets/sheet1.xml").Descendants(Spreadsheet + "row").ToArray();
        Assert.Equal(expected.Length + 1, rows.Length);
        var headers = rows[0].Descendants(Spreadsheet + "t").Select(t => t.Value).ToArray();
        Assert.Equal(6, headers.Length);
        Assert.Contains(model, headers[2]);
        Assert.Contains("W/m²", headers[1]); Assert.Contains("W/m²", headers[2]); Assert.Contains("W/m²", headers[5]);
        for (int i = 0; i < expected.Length; i++)
        {
            var saved = expected[i]; var row = rows[i + 1];
            Assert.Equal(saved.Timestamp.ToString("O", CultureInfo.InvariantCulture), Cell(row, 'A'));
            Assert.Equal(saved.Timestamp.Offset, DateTimeOffset.Parse(Cell(row, 'A')!, CultureInfo.InvariantCulture).Offset);
            Assert.Equal(saved.BeforeDirect, Number(row, 'B'));
            Assert.Equal(saved.BeforeDiffuse, Number(row, 'C'));
            Assert.Equal(saved.DirectTransmission, Number(row, 'D'));
            Assert.Equal(saved.DiffuseTransmission, Number(row, 'E'));
            Assert.Equal(saved.AfterTotal, Number(row, 'F'));
            if (saved.BeforeDirect > 0)
                Assert.Equal(saved.AfterTotal!.Value, Number(row, 'B')!.Value * Number(row, 'D')!.Value + Number(row, 'C')!.Value * Number(row, 'E')!.Value, 12);
        }
        Assert.Null(Cell(rows[1], 'D')); Assert.Null(Cell(rows[1], 'E'));
        Assert.Contains(model, Part(workbook, "docProps/core.xml").Root!.Value);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "export.json")));
        Assert.Equal("Complete", receipt.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public async Task EmptySelectionDoesNotCreateAnExport()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        string parent = Path.Combine(fixture.Root, "exports");
        Assert.Throws<ArgumentException>(() => AppServices.Export(result, parent, new DataExportOptions(false)));
        Assert.False(Directory.Exists(parent));
    }

    [Theory]
    [InlineData(false, TimestampLabel.End)]
    [InlineData(true, TimestampLabel.Start)]
    public async Task SourceBoundsAlignNasaAndOpenMeteoRegardlessOfTheirLabelsAndStudyClipping(bool includePanel, TimestampLabel label)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var firstEnd = new DateTimeOffset(2025, 5, 14, 18, 0, 0, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(2));
        var intervals = Enumerable.Range(0, 28).Select(i =>
        {
            var end = firstEnd.AddHours(i);
            double diffuse = end.Hour is >= 4 and <= 17 ? 100 : 0;
            var start = end.AddHours(-1);
            return new IrradianceInterval($"weather-{i}", label == TimestampLabel.End ? end : start, start, end, start, end, 0, diffuse)
            { GlobalHorizontal = i == 2 ? null : diffuse > 0 ? diffuse + i * 0.01 : 0 };
        }).ToArray();
        string source = label == TimestampLabel.End ? "OpenMeteo" : "NasaPower";
        var weather = new IrradianceDataset(intervals, source, "UTC", $"Synthetic {label}-labeled hourly mean",
            label, TimeSpan.FromHours(1));
        string input = Path.Combine(fixture.Root, "source.xlsx");
        var settings = fixture.Settings with { ImportPath = input, Zone = "India Standard Time" };
        var selected = IrradianceDatasets.SelectCalendarRange(weather, settings.Start, settings.End, TimeZoneSelection.Resolve(settings.Zone));
        IrradianceDatasetFiles.Export(selected, input);
        var result = await Calculate(service, settings);
        // The selected local day clips both boundary hours; the last source label falls on the following local day.
        Assert.Equal(25, result.Run.Rows.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Run.Rows[0].End - result.Run.Rows[0].Start);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Run.Rows[^1].End - result.Run.Rows[^1].Start);
        var fields = IrradianceFields.GlobalHorizontal | (includePanel ? IrradianceFields.ShadedTotal : IrradianceFields.None);
        var modified = result with { Raw = result.Raw with { Intervals = [], Source = "untrusted" } };
        string output = AppServices.Export(modified, fixture.Root, new(Fields: fields, SourceIntervalBounds: true));
        using var workbook = ZipFile.OpenRead(Path.Combine(output, "irradiance.xlsx"));
        var table = Part(workbook, "xl/worksheets/sheet1.xml").Descendants(Spreadsheet + "row").ToArray();
        Assert.Equal(result.Run.Rows.Count + 1, table.Length);
        var headers = table[0].Descendants(Spreadsheet + "t").Select(t => t.Value).ToArray();
        Assert.Equal(includePanel ? ["Timeseries", "Source interval start", "Source interval end", "Final total (W/m²)", "Source GHI (W/m²)"] :
            new[] { "Timeseries", "Source interval start", "Source interval end", "Source GHI (W/m²)" }, headers);
        for (int i = 0; i < result.Run.Rows.Count; i++)
        {
            var original = intervals[i + 1];
            Assert.Equal(original.Timestamp.ToString("O", CultureInfo.InvariantCulture), Cell(table[i + 1], 'A'));
            Assert.Equal(original.SourceStart.ToString("O", CultureInfo.InvariantCulture), Cell(table[i + 1], 'B'));
            Assert.Equal(original.SourceEnd.ToString("O", CultureInfo.InvariantCulture), Cell(table[i + 1], 'C'));
            Assert.Equal(original.GlobalHorizontal, Number(table[i + 1], includePanel ? 'E' : 'D'));
        }
        var metadata = Part(workbook, "xl/worksheets/sheet2.xml").Root!.Value;
        Assert.Contains(weather.Source, metadata); Assert.Contains(weather.TimestampConvention, metadata);
        Assert.Contains(label.ToString(), metadata);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "export.json")));
        Assert.Equal(weather.Source, receipt.RootElement.GetProperty("WeatherSource").GetString());
        Assert.Equal(AppData.FileKey(Path.Combine(fixture.Debug, "03-irradiance/horizontal-irradiance.xlsx")),
            receipt.RootElement.GetProperty("WeatherArtifactSha256").GetString());
        // A results workbook with GHI in column B cannot supply the BHI required by weather import.
        string mixed = AppServices.Export(modified, fixture.Root, new(Fields: IrradianceFields.GlobalHorizontal | IrradianceFields.DirectHorizontal));
        Assert.Throws<InvalidDataException>(() => IrradianceDatasetFiles.Import(Path.Combine(mixed, "irradiance.xlsx"),
            TimeZoneInfo.Utc, TimeSpan.FromHours(1), label));

        // Equal lengths and timestamps alone would accept a different interval identity.
        var mismatched = weather with { Intervals = intervals.Select(r => r with { Id = r.Id + "-other" }).ToArray() };
        using var panel = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Debug, "06-shading/panel-results.json")));
        string bad = Path.Combine(fixture.Root, "mismatch.xlsx");
        Assert.Throws<InvalidOperationException>(() => IrradianceDataExport.WriteWorkbook(bad, panel.RootElement,
            result.ManagedDebugRun!.ExportState(), new(Fields: fields), mismatched));
        Assert.False(File.Exists(bad));
    }

    [Fact]
    public async Task HistoricalComponentsRemainExportableAndDoNotMasqueradeAsOriginalGhi()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        var failure = Assert.Throws<InvalidOperationException>(() => AppServices.Export(result, fixture.Root,
            new(Fields: IrradianceFields.GlobalHorizontal)));
        Assert.Contains("no original GHI", failure.Message);
        Assert.Empty(Directory.GetDirectories(fixture.Root, "scenario-*"));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".solarshade-export-*"));
        string output = AppServices.Export(result, fixture.Root,
            new(Fields: IrradianceFields.DirectHorizontal | IrradianceFields.DiffuseHorizontal));
        using var workbook = ZipFile.OpenRead(Path.Combine(output, "irradiance.xlsx"));
        var table = Part(workbook, "xl/worksheets/sheet1.xml").Descendants(Spreadsheet + "row").ToArray();
        Assert.Equal(new[] { "Timeseries", "Direct horizontal (W/m²)", "Diffuse horizontal (W/m²)" },
            table[0].Descendants(Spreadsheet + "t").Select(t => t.Value));
        Assert.All(table.Skip(1), row => { Assert.Equal(0, Number(row, 'B')); Assert.Equal(100, Number(row, 'C')); });
        var imported = IrradianceDatasetFiles.Import(Path.Combine(output, "irradiance.xlsx"), TimeZoneInfo.Utc,
            TimeSpan.FromHours(1), TimestampLabel.Start);
        Assert.All(imported.Intervals, row => { Assert.Equal(0, row.DirectHorizontal); Assert.Equal(100, row.DiffuseHorizontal); });
    }

    [Theory]
    [InlineData("source")]
    [InlineData("artifact")]
    [InlineData("missing")]
    [InlineData("edit")]
    public async Task StaleOrIncompleteResultsCannotBeExported(string change)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        if (change == "source") File.AppendAllText(fixture.Settings.ImportPath, "\n");
        if (change == "artifact") File.AppendAllText(Path.Combine(fixture.Debug, "06-shading/panel-results.json"), "\n");
        if (change == "missing") File.Delete(Path.Combine(fixture.Debug, "06-shading/panel-shaded.xlsx"));
        if (change == "edit") service.ObserveInputs(fixture.Settings with { PanelTilt = 80 });
        Assert.Throws<InvalidOperationException>(() => IrradianceDataExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Root, "export")));
        NoPublishedExport(fixture.Root);
    }

    [Fact]
    public async Task ExportDestinationsAndLocksPreserveUserFiles()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        string destination = Path.Combine(fixture.Root, "export"); Directory.CreateDirectory(destination);
        string existing = Path.Combine(destination, "irradiance.xlsx"); File.WriteAllText(existing, "user workbook");
        Assert.Throws<IOException>(() => IrradianceDataExport.Publish(result.ManagedDebugRun!, destination));
        Assert.Equal("user workbook", File.ReadAllText(existing)); Assert.Single(Directory.GetFiles(destination));
        Assert.Throws<ArgumentException>(() => IrradianceDataExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Debug, "export")));
        using (var locked = new FileStream(fixture.Settings.ImportPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<Exception>(() => IrradianceDataExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Root, "locked-export")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "locked-export")));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".solarshade-export-*"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("artifact")]
    [InlineData("edit")]
    [InlineData("new update")]
    public async Task ChangesDuringWorkbookGenerationPreventPublication(string change)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var export = Task.Run(() => IrradianceDataExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Root, "export"), _ => {
            entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException();
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
            if (change == "source") File.AppendAllText(fixture.Settings.ImportPath, "\n");
            if (change == "artifact") File.AppendAllText(Path.Combine(fixture.Debug, "06-shading/panel-results.json"), "\n");
            if (change == "edit") service.ObserveInputs(fixture.Settings with { PanelTilt = 80 });
            if (change == "new update") await Calculate(service, fixture.Settings with { PanelTilt = 80 });
        }
        finally { resume.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => export);
        NoPublishedExport(fixture.Root);
    }

    [Fact]
    public async Task FailedWorkbookPublicationCleansUpAndCanBeRetried()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        Assert.Throws<IOException>(() => IrradianceDataExport.Publish(result.ManagedDebugRun!, Path.Combine(fixture.Root, "export"), path => {
            Assert.True(File.Exists(path)); throw new IOException("Simulated full export disk.");
        }));
        NoPublishedExport(fixture.Root); Assert.True(service.IsCurrent(result));
        string folder = AppServices.Export(result, fixture.Root, new DataExportOptions());
        Assert.True(File.Exists(Path.Combine(folder, "irradiance.xlsx")));
    }

    [Theory]
    [InlineData("missing diffuse")]
    [InlineData("unavailable shaded value")]
    [InlineData("nonfinite total")]
    [InlineData("unsupported model")]
    public async Task IncompleteSavedValuesCannotProduceAnApparentlyCompleteWorkbook(string defect)
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var result = await Calculate(service, fixture.Settings);
        var state = result.ManagedDebugRun!.ExportState();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Debug, "06-shading/panel-results.json")))!;
        var first = json["Rows"]![0]!;
        if (defect == "missing diffuse") first.AsObject().Remove("BeforeDiffuse");
        if (defect == "unavailable shaded value") first["AfterDirect"] = null;
        if (defect == "nonfinite total") first["AfterTotal"] = System.Text.Json.Nodes.JsonNode.Parse("1e999");
        if (defect == "unsupported model") json["Model"] = "Unknown";
        using var invalid = JsonDocument.Parse(json.ToJsonString());
        string output = Path.Combine(fixture.Root, "invalid.xlsx");
        Assert.Throws<InvalidOperationException>(() => IrradianceDataExport.WriteWorkbook(output, invalid.RootElement, state));
        Assert.False(File.Exists(output));
    }
}
