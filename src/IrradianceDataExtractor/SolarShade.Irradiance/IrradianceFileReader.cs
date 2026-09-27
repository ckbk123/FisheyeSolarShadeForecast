using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;
namespace SolarShade.Irradiance;
public static partial class IrradianceDatasetFiles
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static DateTimeOffset ParseTime(string text, TimeZoneInfo zone)
    {
        if (Regex.IsMatch(text, @"(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase) && DateTimeOffset.TryParse(text, Inv, DateTimeStyles.None, out var offset)) return offset;
        DateTime local;
        if (double.TryParse(text, NumberStyles.Float, Inv, out var number)) local = DateTime.FromOADate(number);
        else if (!DateTime.TryParseExact(text, ["dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd"], Inv, DateTimeStyles.None, out local))
            throw new InvalidDataException($"Unrecognized timestamp '{text}'. Use ISO 8601, Excel dates, or DD.MM.YYYY HH:mm.");
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local)) throw new InvalidDataException($"Timestamp '{text}' needs an explicit UTC offset because of daylight saving.");
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
    private static List<string[]> Csv(string path)
    {
        using var reader = new TextFieldParser(path); reader.TextFieldType = FieldType.Delimited;
        string first = File.ReadLines(path).FirstOrDefault() ?? "";
        reader.SetDelimiters(first.Contains(';') ? ";" : ","); reader.HasFieldsEnclosedInQuotes = true;
        var rows = new List<string[]>(); while (!reader.EndOfData) rows.Add(reader.ReadFields()!); return rows;
    }
    private static List<string[]> Xlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument Part(string part)
        {
            var entry = zip.GetEntry(part) ?? throw new InvalidDataException("Missing XLSX part " + part);
            if (entry.Length > 128L * 1024 * 1024) throw new InvalidDataException("Workbook sheet is too large.");
            using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128L * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        XNamespace n = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        if (Part("xl/workbook.xml").Descendants(n + "workbookPr").Any(p => (string?)p.Attribute("date1904") is "1" or "true")) throw new InvalidDataException("Convert this 1904-date workbook to ISO timestamp text before importing.");
        var sheet = Part("xl/workbook.xml").Descendants(n + "sheet").First();
        var link = Part("xl/_rels/workbook.xml.rels").Root!.Elements().Single(e => (string?)e.Attribute("Id") == (string?)sheet.Attribute(rel + "id"));
        string target = (string?)link.Attribute("Target") ?? "";
        if (target.Contains("..") || target.Contains('\\') || (string?)link.Attribute("TargetMode") == "External") throw new InvalidDataException("Unsupported sheet target.");
        var strings = zip.GetEntry("xl/sharedStrings.xml") is null ? [] : Part("xl/sharedStrings.xml").Descendants(n + "si").Select(e => string.Concat(e.Descendants(n + "t").Select(t => t.Value))).ToArray();
        return Part(target.StartsWith('/') ? target[1..] : "xl/" + target).Descendants(n + "row").Select(row =>
        {
            var values = new string[26]; Array.Fill(values, "");
            foreach (var cell in row.Elements(n + "c"))
            {
                string address = (string?)cell.Attribute("r") ?? "";
                if (!Regex.IsMatch(address, @"^[A-Z]\d+$")) continue;
                if (cell.Element(n + "f") != null) throw new InvalidDataException("Formula cells are not accepted; export materialized values first.");
                string raw = cell.Element(n + "v")?.Value ?? "";
                values[address[0] - 'A'] = (string?)cell.Attribute("t") switch { "inlineStr" => string.Concat(cell.Descendants(n + "t").Select(t => t.Value)), "s" => strings[int.Parse(raw, Inv)], _ => raw };
            }
            return values;
        }).ToList();
    }
}
