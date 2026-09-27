using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SolarShade.Irradiance.Transposition;

internal static class PlaneWorkbook
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static void Write(string path, IReadOnlyList<TransposedSample> rows, PanelOrientation panel,
        SamplingWindow window, DiffuseModel model, TranspositionOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An .xlsx output path is required.", nameof(path));
        if (rows.Count > 1048575) throw new ArgumentException("Too many rows for one Excel sheet.");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                void Part(string name, string value)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
                    writer.Write(value);
                }
                Part("[Content_Types].xml", """
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>
                    """);
                Part("_rels/.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>
                    """);
                Part("xl/workbook.xml", $"<workbook xmlns=\"{Ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Unshaded panel\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Part("xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                    """);
                Part("xl/styles.xml", $"""
                    <styleSheet xmlns="{Ns}"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF174D59"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
                    """);
                string metadata = FormattableString.Invariant($"Unshaded front-side plane-of-array irradiance; model {model}; tilt {panel.TiltDegrees} degrees from horizontal; front azimuth {panel.AzimuthDegrees} degrees clockwise from true north. Window start offset {window.StartOffsetMinutes} minutes, duration {window.DurationMinutes} minutes, {window.Samples} midpoint samples. Constant daylight DNI inferred from interval BHI; constant interval DHI; no sub-hour cloud observations. No shading, ground reflection, optical or electrical losses. Low-Sun rows: {rows.Count(r => r.Flags.HasFlag(TranspositionFlags.LowSun))}; isotropic night/twilight diffuse rows: {rows.Count(r => r.Flags.HasFlag(TranspositionFlags.NightDiffuseIsotropic))}. Minimum mean cosine {options.MinimumMeanCosine}, maximum inferred DNI {options.MaximumInferredDni} W/m². Time zone: {options.TimeZone.Id}; original label instant preserved; ISO 8601 includes UTC offset. Units W/m². {options.Provenance}");
                XmlConvert.VerifyXmlChars(metadata);
                Part("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Unshaded panel irradiance</dc:title><dc:description>" + System.Security.SecurityElement.Escape(metadata) + "</dc:description></cp:coreProperties>");
                using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
                using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                xml.WriteStartDocument(); xml.WriteStartElement("worksheet", Ns);
                xml.WriteStartElement("dimension"); xml.WriteAttributeString("ref", $"A1:C{rows.Count + 1}"); xml.WriteEndElement();
                xml.WriteStartElement("sheetViews"); xml.WriteStartElement("sheetView"); xml.WriteAttributeString("workbookViewId", "0");
                xml.WriteStartElement("pane"); xml.WriteAttributeString("ySplit", "1"); xml.WriteAttributeString("topLeftCell", "A2"); xml.WriteAttributeString("activePane", "bottomLeft"); xml.WriteAttributeString("state", "frozen");
                xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
                xml.WriteStartElement("cols");
                for (int col = 1; col <= 3; col++)
                {
                    xml.WriteStartElement("col"); xml.WriteAttributeString("min", col.ToString(CultureInfo.InvariantCulture));
                    xml.WriteAttributeString("max", col.ToString(CultureInfo.InvariantCulture)); xml.WriteAttributeString("width", "34"); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement();
                }
                xml.WriteEndElement(); xml.WriteStartElement("sheetData");
                void Text(string address, string value, string style)
                {
                    xml.WriteStartElement("c"); xml.WriteAttributeString("r", address); xml.WriteAttributeString("s", style); xml.WriteAttributeString("t", "inlineStr");
                    xml.WriteStartElement("is"); xml.WriteElementString("t", value); xml.WriteEndElement(); xml.WriteEndElement();
                }
                void Number(string address, double value)
                {
                    xml.WriteStartElement("c"); xml.WriteAttributeString("r", address); xml.WriteAttributeString("s", "2");
                    xml.WriteElementString("v", value.ToString("R", CultureInfo.InvariantCulture)); xml.WriteEndElement();
                }
                xml.WriteStartElement("row"); xml.WriteAttributeString("r", "1");
                Text("A1", "Timestamp (local, UTC offset)", "1"); Text("B1", "Direct on panel (W/m²)", "1"); Text("C1", "Sky diffuse on panel (W/m²)", "1"); xml.WriteEndElement();
                for (int i = 0; i < rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested(); var sample = rows[i]; string row = (i + 2).ToString(CultureInfo.InvariantCulture);
                    xml.WriteStartElement("row"); xml.WriteAttributeString("r", row);
                    Text("A" + row, TimeZoneInfo.ConvertTime(sample.Timestamp, options.TimeZone).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture), "0");
                    Number("B" + row, sample.Direct); Number("C" + row, sample.SkyDiffuse); xml.WriteEndElement();
                }
                xml.WriteEndElement(); xml.WriteStartElement("autoFilter"); xml.WriteAttributeString("ref", $"A1:C{rows.Count + 1}"); xml.WriteEndElement();
                xml.WriteEndElement(); xml.WriteEndDocument();
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
