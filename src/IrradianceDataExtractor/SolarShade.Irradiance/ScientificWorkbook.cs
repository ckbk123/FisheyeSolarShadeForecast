using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SolarShade.Irradiance;

/// <summary>Mechanical workbook transport. Scientific schemas and calculations belong to each stage exporter.</summary>
public sealed record ScientificSheet(string Name, IReadOnlyList<string> Headers, IEnumerable<object?[]> Rows);
public static class ScientificWorkbook
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static void Write(string path, string sheetName, IReadOnlyList<string> headers,
        IEnumerable<object?[]> rows, string metadata, CancellationToken ct = default)
        => Write(path, [new ScientificSheet(sheetName, headers, rows)], metadata, ct);

    public static void Write(string path, IReadOnlyList<ScientificSheet> sheets, string metadata, CancellationToken ct = default)
    {
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be .xlsx.");
        if (sheets.Count == 0 || sheets.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sheets.Count)
            throw new ArgumentException("Unique worksheet names required.");
        foreach (var s in sheets)
            if (s.Name.Length is < 1 or > 31 || s.Name.IndexOfAny(['[',']',':','*','?','/','\\']) >= 0 || s.Headers.Count is < 1 or > 16384)
                throw new ArgumentException("Invalid worksheet name or column count.");
        XmlConvert.VerifyXmlChars(metadata);
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                void Part(string name, string content)
                { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false)); writer.Write(content); }
                string Escape(string value) { XmlConvert.VerifyXmlChars(value); return System.Security.SecurityElement.Escape(value)!; }
                Part("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" + string.Concat(sheets.Select((_,i) => $"<Override PartName=\"/xl/worksheets/sheet{i+1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) + "</Types>");
                Part("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
                Part("xl/workbook.xml", $"<workbook xmlns=\"{Ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" + string.Concat(sheets.Select((s,i) => $"<sheet name=\"{Escape(s.Name)}\" sheetId=\"{i+1}\" r:id=\"rId{i+1}\"/>")) + "</sheets></workbook>");
                Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + string.Concat(sheets.Select((_,i) => $"<Relationship Id=\"rId{i+1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i+1}.xml\"/>")) + $"<Relationship Id=\"rId{sheets.Count+1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Part("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description>" + Escape(metadata) + "</dc:description></cp:coreProperties>");
                Part("xl/styles.xml", $"<styleSheet xmlns=\"{Ns}\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF174D59\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                for (int i = 0; i < sheets.Count; i++)
                {
                    ct.ThrowIfCancellationRequested(); var sheet = sheets[i];
                    using var stream = zip.CreateEntry($"xl/worksheets/sheet{i+1}.xml", CompressionLevel.Fastest).Open();
                    using var xml = XmlWriter.Create(stream, new() { Encoding = new UTF8Encoding(false) });
                    xml.WriteStartElement("worksheet", Ns);
                    xml.WriteStartElement("sheetViews"); xml.WriteStartElement("sheetView"); xml.WriteAttributeString("workbookViewId", "0");
                    xml.WriteStartElement("pane"); xml.WriteAttributeString("ySplit", "1"); xml.WriteAttributeString("topLeftCell", "A2"); xml.WriteAttributeString("state", "frozen"); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
                    xml.WriteStartElement("cols"); xml.WriteStartElement("col"); xml.WriteAttributeString("min", "1"); xml.WriteAttributeString("max", sheet.Headers.Count.ToString()); xml.WriteAttributeString("width", "28"); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement(); xml.WriteEndElement();
                    xml.WriteStartElement("sheetData");
                    int rowNumber = 0;
                    void Row(object?[] values, bool header)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++rowNumber > 1048576) throw new ArgumentException("Too many rows for a worksheet.");
                        if (values.Length != sheet.Headers.Count) throw new ArgumentException("Workbook row width differs from headers.");
                        xml.WriteStartElement("row"); xml.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
                        for (int col = 0; col < values.Length; col++)
                        {
                            var value = values[col]; if (value == null) continue;
                            xml.WriteStartElement("c"); xml.WriteAttributeString("r", Column(col + 1) + rowNumber); if (header) xml.WriteAttributeString("s", "1");
                            if (value is double or float or decimal or int or long or uint or ulong or short or ushort or byte or sbyte)
                            {
                                if (!double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture))) throw new ArgumentException("Nonfinite workbook value.");
                                xml.WriteElementString("v", Convert.ToString(value, CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                string text = value is DateTimeOffset instant ? instant.ToString("O", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                                xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is"); xml.WriteElementString("t", text); xml.WriteEndElement();
                            }
                            xml.WriteEndElement();
                        }
                        xml.WriteEndElement();
                    }
                    Row(sheet.Headers.Cast<object?>().ToArray(), true);
                    foreach (var row in sheet.Rows) Row(row, false);
                    xml.WriteEndElement(); xml.WriteStartElement("autoFilter"); xml.WriteAttributeString("ref", $"A1:{Column(sheet.Headers.Count)}{rowNumber}"); xml.WriteEndElement(); xml.WriteEndElement();
                }
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Column(int index)
    { string result = ""; while (index > 0) { index--; result = (char)('A' + index % 26) + result; index /= 26; } return result; }
}
