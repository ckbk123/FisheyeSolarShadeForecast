using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SolarShade.Irradiance;

// Streaming Open XML writer: no Excel installation, native dependency, or workbook object graph.
internal static class XlsxExporter
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static void Write(string path, IReadOnlyList<IrradianceSample> rows, TimeZoneInfo zone,
        IrradianceRequest request, string convention, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                void Part(string name, string xml)
                { using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false)); writer.Write(xml); }
                Part("[Content_Types].xml", """
                    <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>
                    """);
                Part("_rels/.rels", """
                    <?xml version="1.0" encoding="utf-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>
                    """);
                Part("xl/workbook.xml", $"<workbook xmlns=\"{Ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Irradiance\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Part("xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                    """);
                Part("xl/styles.xml", $"""
                    <styleSheet xmlns="{Ns}"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF174D59"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
                    """);
                var source = ProviderCatalog.All.Single(x => x.Service == request.Service);
                Part("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Horizontal irradiance</dc:title><dc:description>" +
                    System.Security.SecurityElement.Escape(FormattableString.Invariant($"Source: {source.DisplayName}; {source.RegistrationUrl}. Requested latitude {request.Latitude}, longitude {request.Longitude}, local dates [{request.StartDate:yyyy-MM-dd}, {request.EndDate:yyyy-MM-dd}). {source.UsageNotice} Time zone: {zone.Id}. {convention} ISO 8601 local timestamps include UTC offset; numeric columns W/m².")) + "</dc:description></cp:coreProperties>");
                using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
                using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                xml.WriteStartDocument(); xml.WriteStartElement("worksheet", Ns);
                xml.WriteStartElement("dimension"); xml.WriteAttributeString("ref", $"A1:C{rows.Count + 1}"); xml.WriteEndElement();
                xml.WriteStartElement("sheetViews"); xml.WriteStartElement("sheetView"); xml.WriteAttributeString("workbookViewId", "0");
                xml.WriteStartElement("pane"); xml.WriteAttributeString("ySplit", "1"); xml.WriteAttributeString("topLeftCell", "A2"); xml.WriteAttributeString("activePane", "bottomLeft"); xml.WriteAttributeString("state", "frozen");
                xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
                xml.WriteStartElement("cols");
                for (int col = 1; col <= 3; col++)
                { xml.WriteStartElement("col"); xml.WriteAttributeString("min", col.ToString()); xml.WriteAttributeString("max", col.ToString()); xml.WriteAttributeString("width", col == 1 ? "32" : "31"); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement(); }
                xml.WriteEndElement(); xml.WriteStartElement("sheetData");
                void Text(string address, string value, string style)
                { xml.WriteStartElement("c"); xml.WriteAttributeString("r", address); xml.WriteAttributeString("s", style); xml.WriteAttributeString("t", "inlineStr"); xml.WriteStartElement("is"); xml.WriteElementString("t", value); xml.WriteEndElement(); xml.WriteEndElement(); }
                void Number(string address, double value)
                { xml.WriteStartElement("c"); xml.WriteAttributeString("r", address); xml.WriteAttributeString("s", "2"); xml.WriteElementString("v", value.ToString("R", CultureInfo.InvariantCulture)); xml.WriteEndElement(); }
                xml.WriteStartElement("row"); xml.WriteAttributeString("r", "1");
                Text("A1", "Timestamp (local, UTC offset)", "1"); Text("B1", "Direct horizontal (W/m²)", "1"); Text("C1", "Diffuse horizontal (W/m²)", "1"); xml.WriteEndElement();
                for (int i = 0; i < rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested(); var sample = rows[i]; string row = (i + 2).ToString(CultureInfo.InvariantCulture);
                    xml.WriteStartElement("row"); xml.WriteAttributeString("r", row);
                    Text("A" + row, TimeZoneInfo.ConvertTime(sample.TimestampUtc, zone).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), "0");
                    Number("B" + row, sample.DirectHorizontal); Number("C" + row, sample.DiffuseHorizontal); xml.WriteEndElement();
                }
                xml.WriteEndElement(); xml.WriteStartElement("autoFilter"); xml.WriteAttributeString("ref", $"A1:C{rows.Count + 1}"); xml.WriteEndElement();
                xml.WriteEndElement(); xml.WriteEndDocument();
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
