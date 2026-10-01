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
}
