using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using SolarShade.Irradiance;
using Xunit;

public class ScientificRowWriterTests
{
    [Fact]
    public void TypedRowsMatchStandardTransportUnderNonEnglishCulture()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var prior = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var random = new Random(17); var values = Enumerable.Range(0, 1000).Select(_ => random.NextDouble() * 1e12 - 1e8).Concat([double.Epsilon, double.MaxValue, -0d]).ToArray();
            string[] headers = ["number", "text", "time", "empty"];
            var stamp = new DateTimeOffset(2025, 6, 3, 11, 12, 13, TimeSpan.FromHours(7)).AddTicks(1234567);
            const string label = "<&test>  Ω\r\n";
            ScientificWorkbook.Write(Path.Combine(root, "old.xlsx"), "data", headers, values.Select(n => new object?[] { n, label, stamp, null }), "fixture");
            ScientificWorkbook.Write(Path.Combine(root, "new.xlsx"), [new ScientificSheet("data", headers, []) { WriteRows = (w, ct) =>
            { foreach (double n in values) { w.Begin(); w.Number(n); w.Text(label); w.Instant(stamp); w.Text(null); w.End(); } } }], "fixture");
            string[] Cells(string path)
            {
                using var zip = ZipFile.OpenRead(path); using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                var doc = XDocument.Load(stream); XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                return doc.Descendants(ns + "c").Select(c => c.Attribute("r")!.Value + ":" + c.Value).ToArray();
            }
            Assert.Equal(Cells(Path.Combine(root, "old.xlsx")), Cells(Path.Combine(root, "new.xlsx")));
        }
        finally { CultureInfo.CurrentCulture = prior; Directory.Delete(root, true); }
    }
    [Fact]
    public void CancellationAndInvalidWidthDoNotReplaceExistingWorkbook()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx"); File.WriteAllText(path, "original");
        try
        {
            Assert.Throws<ArgumentException>(() => ScientificWorkbook.Write(path, [new ScientificSheet("data", ["a", "b"], [])
                { WriteRows = (w, ct) => { w.Begin(); w.Number(1); w.End(); } }], "fixture"));
            using var cts = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => ScientificWorkbook.Write(path, [new ScientificSheet("data", ["a"], [])
                { WriteRows = (w, ct) => { w.Begin(); w.Number(1); w.End(); cts.Cancel(); w.Begin(); } }], "fixture", cts.Token));
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void OptionalColumnWidthsAreInvariantAndOtherSheetsKeepTheirDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        var prior = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            string[] headers = ["timestamp", "number", "text"];
            object?[][] rows = [["2025-06-03T11:12:13+07:00", 1.25, "fixture"]];
            ScientificWorkbook.Write(path, [
                new ScientificSheet("custom", headers, rows) { ColumnWidths = [36, 24.5, 255] },
                new ScientificSheet("default", headers, rows)
            ], "fixture");
            using var zip = ZipFile.OpenRead(path);
            XDocument Sheet(int number)
            { using var stream = zip.GetEntry($"xl/worksheets/sheet{number}.xml")!.Open(); return XDocument.Load(stream); }
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var custom = Sheet(1); var standard = Sheet(2);
            var columns = custom.Descendants(ns + "col").ToArray();
            Assert.Equal(new[] { "36", "24.5", "255" }, columns.Select(c => (string?)c.Attribute("width")));
            Assert.Equal(new[] { "1", "2", "3" }, columns.Select(c => (string?)c.Attribute("min")));
            Assert.Equal(new[] { "1", "2", "3" }, columns.Select(c => (string?)c.Attribute("max")));
            Assert.All(columns, c => Assert.Equal("1", (string?)c.Attribute("customWidth")));
            var defaultColumn = Assert.Single(standard.Descendants(ns + "col"));
            Assert.Equal("1", (string?)defaultColumn.Attribute("min"));
            Assert.Equal("3", (string?)defaultColumn.Attribute("max"));
            Assert.Equal("28", (string?)defaultColumn.Attribute("width"));
            Assert.True(XNode.DeepEquals(custom.Root!.Element(ns + "sheetData"), standard.Root!.Element(ns + "sheetData")));
            Assert.True(XNode.DeepEquals(custom.Root!.Element(ns + "sheetViews"), standard.Root!.Element(ns + "sheetViews")));
            Assert.True(XNode.DeepEquals(custom.Root!.Element(ns + "autoFilter"), standard.Root!.Element(ns + "autoFilter")));
        }
        finally { CultureInfo.CurrentCulture = prior; File.Delete(path); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(255.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidColumnWidthDoesNotReplaceExistingWorkbook(double width)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx"); File.WriteAllText(path, "original");
        try
        {
            Assert.Throws<ArgumentException>(() => ScientificWorkbook.Write(path,
                [new ScientificSheet("data", ["a"], []) { ColumnWidths = [width] }], "fixture"));
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ColumnWidthsMustMatchTheHeaderCount(int count)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            Assert.Throws<ArgumentException>(() => ScientificWorkbook.Write(path,
                [new ScientificSheet("data", ["a", "b"], []) { ColumnWidths = Enumerable.Repeat(24d, count).ToArray() }], "fixture"));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
