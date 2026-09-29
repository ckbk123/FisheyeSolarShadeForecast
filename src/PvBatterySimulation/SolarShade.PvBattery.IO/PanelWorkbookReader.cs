using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace SolarShade.PvBattery.IO;

/// <summary>Reads the explicit-interval panel-shaded.xlsx schema emitted by ExportPanel.</summary>
public static class PanelWorkbookReader
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly string[] Required = ["Timestamp", "Shaded total on panel (W/m²)", "Interval ID",
        "Source start", "Source end", "Interval start", "Interval end"];

    public static ShadedIrradianceSeries Read(string path, string timeZoneId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var file = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        XDocument Part(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Missing workbook part '{name}'.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        var sheets = Part("xl/workbook.xml").Descendants(Ns + "sheet").Where(s => (string?)s.Attribute("name") == "Panel results").ToArray();
        if (sheets.Length != 1) throw new InvalidDataException("Expected exactly one 'Panel results' sheet.");
        string id = (string?)sheets[0].Attribute(Rel + "id") ?? throw new InvalidDataException("Missing sheet relationship.");
        var links = Part("xl/_rels/workbook.xml.rels").Root!.Elements()
            .Where(r => (string?)r.Attribute("Id") == id).ToArray();
        if (links.Length != 1 || (string?)links[0].Attribute("TargetMode") == "External")
            throw new InvalidDataException("Invalid sheet relationship.");
        string target = (string?)links[0].Attribute("Target") ?? throw new InvalidDataException("Missing sheet target.");
        var partUri = new Uri(new Uri("https://workbook.invalid/xl/workbook.xml"), target);
        if (partUri.Host != "workbook.invalid") throw new InvalidDataException("External sheet target is unsupported.");
        string sheetPath = Uri.UnescapeDataString(partUri.AbsolutePath.TrimStart('/'));
        string[] shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] :
            Part("xl/sharedStrings.xml").Descendants(Ns + "si").Select(s => string.Concat(s.Descendants(Ns + "t").Select(t => t.Value))).ToArray();

        Dictionary<string, string> Cells(XElement row)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cell in row.Elements(Ns + "c"))
            {
                ct.ThrowIfCancellationRequested();
                if (cell.Element(Ns + "f") != null) throw new InvalidDataException("Formula cells are unsupported; export calculated panel values.");
                string address = (string?)cell.Attribute("r") ?? throw new InvalidDataException("Missing cell address.");
                string column = new(address.TakeWhile(c => c is >= 'A' and <= 'Z').ToArray());
                if (column.Length == 0) throw new InvalidDataException("Invalid cell address.");
                string kind = (string?)cell.Attribute("t") ?? "n";
                string raw = cell.Element(Ns + "v")?.Value ?? "";
                string value = kind switch
                {
                    "inlineStr" => string.Concat(cell.Element(Ns + "is")?.Descendants(Ns + "t").Select(t => t.Value) ?? []),
                    "s" => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < shared.Length
                        ? shared[index] : throw new InvalidDataException("Invalid shared string index."),
                    "n" or "str" => raw,
                    _ => throw new InvalidDataException($"Unsupported cell type '{kind}'.")
                };
                if (!values.TryAdd(column, value)) throw new InvalidDataException("Duplicate cell column.");
            }
            return values;
        }
        var rows = Part(sheetPath).Descendants(Ns + "sheetData").Elements(Ns + "row").GetEnumerator();
        using (rows)
        {
            if (!rows.MoveNext()) throw new InvalidDataException("Workbook is empty.");
            var headers = Cells(rows.Current);
            var columns = new Dictionary<string, string>();
            foreach (string name in Required)
            {
                var matches = headers.Where(c => c.Value == name).Select(c => c.Key).ToArray();
                if (matches.Length != 1) throw new InvalidDataException($"Expected one '{name}' column. Use the current shaded panel export with explicit intervals.");
                columns[name] = matches[0];
            }
            var intervals = new List<ShadedIrradianceInterval>();
            while (rows.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var cells = Cells(rows.Current);
                if (cells.Count == 0) continue; // Formatting-only rows have no scientific values.
                string Value(string name) => cells.TryGetValue(columns[name], out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value : throw new InvalidDataException($"Missing '{name}' at worksheet row {(string?)rows.Current.Attribute("r")}.");
                DateTimeOffset Instant(string name)
                {
                    string text = Value(name);
                    // Offset-free text and numeric Excel dates are ambiguous and must not inherit the machine timezone.
                    if (!DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
                        throw new InvalidDataException($"'{name}' must be an ISO timestamp with an explicit UTC offset.");
                    return instant;
                }
                if (!double.TryParse(Value("Shaded total on panel (W/m²)"), NumberStyles.Float, CultureInfo.InvariantCulture, out double irradiance))
                    throw new InvalidDataException("Invalid shaded irradiance value.");
                intervals.Add(new(Value("Interval ID"), Instant("Interval start"), Instant("Interval end"), irradiance)
                {
                    SourceStart = Instant("Source start"), SourceEnd = Instant("Source end"), SourceLabel = Instant("Timestamp")
                });
            }
            string? metadata = zip.GetEntry("docProps/core.xml") is null ? null :
                Part("docProps/core.xml").Descendants(XName.Get("description", "http://purl.org/dc/elements/1.1/")).FirstOrDefault()?.Value;
            var result = new ShadedIrradianceSeries(intervals, timeZoneId) { SourceFingerprint = hash, SourceDescription = metadata };
            BatterySimulator.ValidateSeries(result, ct);
            return result;
        }
    }
}
