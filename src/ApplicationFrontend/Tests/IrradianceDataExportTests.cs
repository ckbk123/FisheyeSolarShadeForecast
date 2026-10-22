using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using SolarShade.Desktop;
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
