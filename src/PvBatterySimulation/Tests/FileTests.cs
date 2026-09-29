using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SolarShade.Irradiance;
using SolarShade.PvBattery.IO;
using Xunit;
using static SolarShade.PvBattery.Tests.BatterySimulatorTests;

namespace SolarShade.PvBattery.Tests;

public class FileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pv-battery-tests-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string name) { Directory.CreateDirectory(directory); return Path.Combine(directory, name); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private static readonly string[] Headers = ["Timestamp", "Shaded total on panel (W/m²)", "Interval ID", "Source start", "Source end", "Interval start", "Interval end"];
    private static object?[] Row() => [Start.AddHours(1), 800, "source-1", Start, Start.AddHours(1), Start.AddMinutes(15), Start.AddHours(1)];

    [Fact]
    public void WorkbookUsesSelectedBoundsAndPreservesEndLabelAndProvenance()
    {
        string path = PathFor("panel-shaded.xlsx");
        ScientificWorkbook.Write(path, "Panel results", Headers, [Row()], "fixture source");
        var series = PanelWorkbookReader.Read(path, "Asia/Ho_Chi_Minh");
        var row = Assert.Single(series.Intervals);
        Assert.Equal(Start.AddMinutes(15), row.Start);
        Assert.Equal(Start.AddHours(1), row.SourceLabel);
        Assert.Equal(Start, row.SourceStart);
        Assert.Equal(800, row.MeanWm2);
        Assert.Equal("fixture source", series.SourceDescription);
        Assert.Equal(64, series.SourceFingerprint!.Length);
        Assert.Equal(600, BatterySimulator.Compute(new(Request(0, 0).Settings, series)).Summary.PvWh);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(1, "NaN")]
    [InlineData(1, "-1")]
    [InlineData(2, "")]
    [InlineData(5, "2025-01-01T00:15:00")]
    [InlineData(6, "45658.04167")]
    public void RejectsMissingBadOrAmbiguousWorkbookData(int column, string badValue)
    {
        var row = Row(); row[column] = badValue;
        string path = PathFor("bad.xlsx");
        ScientificWorkbook.Write(path, "Panel results", Headers, [row], "");
        Assert.Throws<InvalidDataException>(() => PanelWorkbookReader.Read(path, "UTC"));
    }

    [Fact]
    public void RejectsUnshadedOrLegacyWorkbookRatherThanGuessingIntervals()
    {
        string path = PathFor("unshaded.xlsx");
        ScientificWorkbook.Write(path, "Panel results", ["Timestamp", "Total (W/m²)"], [new object?[] { Start, 800 }], "");
        Assert.Throws<InvalidDataException>(() => PanelWorkbookReader.Read(path, "UTC"));
    }

    [Fact]
    public void SharedStringsAndSheetRelationshipsAreResolved()
    {
        string path = PathFor("shared.xlsx");
        ScientificWorkbook.Write(path, [
            new("Other", ["Ignore"], [new object?[] { "unused" }]),
            new("Panel results", Headers, [Row()])], "");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet2.xml")!;
            XDocument document;
            using (var stream = entry.Open()) document = XDocument.Load(stream);
            XNamespace ns = document.Root!.Name.Namespace;
            var shared = new XElement(ns + "sst");
            int index = 0;
            foreach (var cell in document.Descendants(ns + "c").Where(c => (string?)c.Attribute("t") == "inlineStr"))
            {
                shared.Add(new XElement(ns + "si", new XElement(ns + "t", cell.Element(ns + "is")!.Value)));
                cell.SetAttributeValue("t", "s"); cell.RemoveNodes(); cell.Add(new XElement(ns + "v", index++));
            }
            entry.Delete();
            using (var stream = zip.CreateEntry("xl/worksheets/sheet2.xml").Open()) document.Save(stream);
            using (var stream = zip.CreateEntry("xl/sharedStrings.xml").Open()) shared.Save(stream);
        }
        Assert.Equal(800, Assert.Single(PanelWorkbookReader.Read(path, "UTC").Intervals).MeanWm2);
    }

    [Fact]
    public void CachedFormulaIsRejected()
    {
        string path = PathFor("formula.xlsx");
        ScientificWorkbook.Write(path, "Panel results", Headers, [Row()], "");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
            XDocument document;
            using (var stream = entry.Open()) document = XDocument.Load(stream);
            XNamespace ns = document.Root!.Name.Namespace;
            document.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "B2").AddFirst(new XElement(ns + "f", "400*2"));
            entry.Delete();
            using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); document.Save(output);
        }
        Assert.Throws<InvalidDataException>(() => PanelWorkbookReader.Read(path, "UTC"));
    }

    [Fact]
    public void JsonInputRoundtripAndRequiredProperties()
    {
        string path = PathFor("request.json");
        var input = Request(200, 100);
        File.WriteAllText(path, JsonSerializer.Serialize(input));
        Assert.Equal(BatterySimulator.Compute(input).InputFingerprint, BatterySimulator.Compute(BatteryFiles.ReadRequest(path)).InputFingerprint);
        foreach (string property in new[] { "PanelAreaM2", "PanelEfficiency", "ConversionEfficiency", "BatteryCapacityWh", "InitialSoc" })
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(input))!;
            node["Settings"]!.AsObject().Remove(property);
            File.WriteAllText(path, node.ToJsonString());
            Assert.Throws<JsonException>(() => BatteryFiles.ReadRequest(path));
        }
        var missingIrradiance = JsonNode.Parse(JsonSerializer.Serialize(input))!;
        missingIrradiance["Irradiance"]!["Intervals"]![0]!.AsObject().Remove("MeanWm2");
        File.WriteAllText(path, missingIrradiance.ToJsonString());
        Assert.Throws<JsonException>(() => BatteryFiles.ReadRequest(path));
        File.WriteAllText(path, JsonSerializer.Serialize(input).Replace("2025-01-01T00:00:00+00:00", "2025-01-01T00:00:00"));
        Assert.Throws<JsonException>(() => BatteryFiles.ReadRequest(path));
    }

    [Fact]
    public void ExportsExactReturnedResultsWithInvariantNumbersAndAuditMetadata()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var result = BatterySimulator.Compute(Request(12.5, 3));
            BatteryFiles.WriteCsv(PathFor("result.csv"), result);
            BatteryFiles.WriteJson(PathFor("result.json"), result);
            BatteryFiles.WriteXlsx(PathFor("result.xlsx"), result);
            string csv = File.ReadAllText(PathFor("result.csv"));
            Assert.Contains("\"12.5\"", csv);
            Assert.Equal(2, File.ReadAllLines(PathFor("result.csv")).Length);
            using var json = JsonDocument.Parse(File.ReadAllText(PathFor("result.json")));
            Assert.Equal(509.5, json.RootElement.GetProperty("Hours")[0].GetProperty("EndStoredWh").GetDouble());
            Assert.Equal(result.InputFingerprint, json.RootElement.GetProperty("InputFingerprint").GetString());
            using var zip = ZipFile.OpenRead(PathFor("result.xlsx"));
            using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var doc = XDocument.Load(sheet);
            XNamespace ns = doc.Root!.Name.Namespace;
            Assert.Equal(result.Hours[0].EndSocPercent, double.Parse(doc.Descendants(ns + "c")
                .Single(c => (string?)c.Attribute("r") == "F2").Element(ns + "v")!.Value, CultureInfo.InvariantCulture));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void CancelledExportsKeepPriorOutput()
    {
        var result = BatterySimulator.Compute(Request(0, 0));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        foreach (var export in new Action<string, BatterySimulationResult, CancellationToken>[] { BatteryFiles.WriteCsv, BatteryFiles.WriteJson, BatteryFiles.WriteXlsx })
        {
            string path = PathFor("prior.xlsx"); File.WriteAllText(path, "prior");
            Assert.Throws<OperationCanceledException>(() => export(path, result, cancel.Token));
            Assert.Equal("prior", File.ReadAllText(path));
        }
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
}
