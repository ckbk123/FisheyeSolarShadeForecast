using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SolarShade.Irradiance;

namespace SolarShade.Shading;

public sealed record IrradianceWorkbook(IReadOnlyList<IrradianceSample> Samples, string Metadata,
    TimeSampling? SuggestedSampling, double? Latitude, double? Longitude)
{
    public IrradianceDataset? Dataset { get; init; }
}

/// <summary>Separate file adapter. Consumes the extractor's three-column XLSX without Excel/COM.
/// ISO 8601 offset text is mandatory; serial dates cannot recover a time zone and are rejected.</summary>
public static class ShadingWorkbook
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static IReadOnlyList<DateTimeOffset> ReadTimestamps(string path) => ReadIrradiance(path).Samples.Select(s => s.TimestampUtc).ToArray();

    public static IrradianceWorkbook ReadIrradiance(string path)
    {
        using var file = File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        XDocument Part(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Missing XLSX part {name}.");
            if (entry.Length > 128*1024*1024) throw new InvalidDataException("XLSX part too large.");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128*1024*1024 });
            return XDocument.Load(reader);
        }
        XNamespace n = Ns, rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sheet = Part("xl/workbook.xml").Descendants(n+"sheet").FirstOrDefault() ?? throw new InvalidDataException("Workbook contains no sheet.");
        var id = (string?)sheet.Attribute(rel+"id");
        var relationship = Part("xl/_rels/workbook.xml.rels").Root!.Elements().Single(e => (string?)e.Attribute("Id") == id);
        var target = (string?)relationship.Attribute("Target") ?? "";
        if ((string?)relationship.Attribute("TargetMode") == "External" || target.Contains("..") || target.Contains('\\')) throw new InvalidDataException("Unsupported worksheet relationship.");
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/"+target;
        var shared = zip.GetEntry("xl/sharedStrings.xml") == null ? [] : Part("xl/sharedStrings.xml").Descendants(n+"si").Select(e => string.Concat(e.Descendants(n+"t").Select(t=>t.Value))).ToArray();
        string Value(XElement c)
        {
            if (c.Element(n+"f") != null) throw new InvalidDataException("Formula inputs are unsupported: export materialized values.");
            var raw = c.Element(n+"v")?.Value ?? "";
            return (string?)c.Attribute("t") switch { "inlineStr" => string.Concat(c.Descendants(n+"t").Select(t=>t.Value)), "s" => shared[int.Parse(raw,Inv)], _ => raw };
        }
        var rows = Part(sheetPath).Descendants(n+"row").ToArray();
        if (rows.Length < 2) throw new InvalidDataException("No irradiance rows.");
        var header = rows[0].Elements(n+"c").Select(Value).ToArray();
        if (header.Length >= 16 && header[3] == "Interval ID" && header[4] == "Source start")
        {
            // Reuse the authoritative native schema reader rather than discard interval metadata.
            var data = IrradianceDatasetFiles.Import(path, TimeZoneInfo.Utc, TimeSpan.FromHours(1), TimestampLabel.Start);
            if (data.Intervals.Any(r => r.Start != r.SourceStart || r.End != r.SourceEnd))
                throw new InvalidDataException("Selected/clipped interval workbooks require SolarPositionModule.PrepareIntervals; the legacy uniform-window API cannot preserve clipping.");
            TimeSpan duration = data.NativeCadence ?? data.Intervals[0].SourceEnd - data.Intervals[0].SourceStart;
            double offset = (data.Intervals[0].SourceStart - data.Intervals[0].Timestamp).TotalMinutes;
            if (data.Intervals.Any(r => r.SourceEnd - r.SourceStart != duration || (r.SourceStart - r.Timestamp).TotalMinutes != offset))
                throw new InvalidDataException("Variable interval bounds require SolarPositionModule.PrepareIntervals; the legacy uniform-window API cannot represent them.");
            return new(Array.AsReadOnly(data.Intervals.Select(r => new IrradianceSample(r.Timestamp, r.DirectHorizontal, r.DiffuseHorizontal)).ToArray()),
                $"{data.Source}; {data.TimestampConvention}; source interval means; {duration.TotalMinutes.ToString(Inv)} minute intervals.",
                new TimeSampling(offset, duration.TotalMinutes, 60), data.Latitude, data.Longitude) { Dataset = data };
        }
        if (header.Length != 3 || !header[0].StartsWith("Timestamp",StringComparison.Ordinal) ||
            !header[1].StartsWith("Direct horizontal",StringComparison.Ordinal) || !header[2].StartsWith("Diffuse horizontal",StringComparison.Ordinal))
            throw new InvalidDataException("Expected extractor columns: Timestamp, Direct horizontal (W/m²), Diffuse horizontal (W/m²).");
        var samples = new IrradianceSample[rows.Length-1];
        for (int i = 1; i < rows.Length; i++)
        {
            var cells = rows[i].Elements(n+"c").ToArray();
            if (cells.Length != 3 || !((string?)cells[0].Attribute("r"))!.StartsWith("A") || !((string?)cells[1].Attribute("r"))!.StartsWith("B") || !((string?)cells[2].Attribute("r"))!.StartsWith("C"))
                throw new InvalidDataException($"Invalid or missing cells in data row {i+1}.");
            var text = Value(cells[0]);
            if (!Regex.IsMatch(text,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant) ||
                !DateTimeOffset.TryParse(text,Inv,DateTimeStyles.None,out var timestamp))
                throw new InvalidDataException($"Row {i+1}: ISO timestamp with explicit UTC offset required.");
            if (!double.TryParse(Value(cells[1]),NumberStyles.Float,Inv,out var direct) || !double.TryParse(Value(cells[2]),NumberStyles.Float,Inv,out var diffuse) ||
                !double.IsFinite(direct) || !double.IsFinite(diffuse) || direct < 0 || diffuse < 0)
                throw new InvalidDataException($"Row {i+1}: invalid irradiance.");
            if (i > 1 && timestamp <= samples[i-2].TimestampUtc) throw new InvalidDataException("Duplicate or decreasing timestamp instants.");
            samples[i-1] = new(timestamp,direct,diffuse);
        }
        var metadata = zip.GetEntry("docProps/core.xml") == null ? "" : string.Join(" ",Part("docProps/core.xml").Descendants().Where(e => e.Name.LocalName == "description").Select(e=>e.Value));
        double? Coordinate(string name)
        {
            var match = Regex.Match(metadata,name+@"\s+([-+\d.eE]+)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            return match.Success && double.TryParse(match.Groups[1].Value.TrimEnd('.'),NumberStyles.Float,Inv,out var value) ? value : null;
        }
        var knownEnd = metadata.Contains("interval end",StringComparison.OrdinalIgnoreCase) || metadata.Contains("preceding-hour",StringComparison.OrdinalIgnoreCase);
        var knownStart = metadata.Contains("NASA POWER",StringComparison.OrdinalIgnoreCase);
        return new(Array.AsReadOnly(samples),metadata,knownStart ? TimeSampling.FollowingHour() : knownEnd ? TimeSampling.PrecedingHour() : null,Coordinate("latitude"),Coordinate("longitude"));
    }

    /// <summary>Atomic XLSX publication, preserving timestamp offsets and explicit missing-coverage cells.</summary>
    public static void Write(string path, ShadingResult result, SolarSite site, CameraPose pose, string provenance,
        CancellationToken cancellationToken = default)
        => WriteCore(path,result,site,pose,provenance,null,cancellationToken);

    public static void WriteSolarPositions(string path,IReadOnlyList<SolarPositionRow> positions,SolarSite site,
        CancellationToken cancellationToken = default)
        => WriteCore(path,null,site,new(),"",positions,cancellationToken);

    private static void WriteCore(string path, ShadingResult? result, SolarSite site, CameraPose pose, string provenance,
        IReadOnlyList<SolarPositionRow>? positions, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(path),".xlsx",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be .xlsx.");
        if ((positions?.Count ?? result!.Rows.Count) >= 1048576) throw new ArgumentException("Output exceeds one worksheet.");
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using (var file = new FileStream(temp,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            using (var zip = new ZipArchive(file,ZipArchiveMode.Create))
            {
                void Part(string name,string text) { using var w = new StreamWriter(zip.CreateEntry(name,CompressionLevel.Fastest).Open(),new UTF8Encoding(false)); w.Write(text); }
                Part("[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                Part("_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Part("xl/workbook.xml",$"<workbook xmlns=\"{Ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{(positions == null ? "Shading" : "Solar positions")}\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Method\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
                Part("xl/_rels/workbook.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Part("xl/styles.xml",$"<styleSheet xmlns=\"{Ns}\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"0.000000\"/></numFmts><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/><color rgb=\"FFFFFFFF\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF17365D\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"4\"><xf fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                void Sheet(string name,IEnumerable<object?[]> rows,double[] widths)
                {
                    using var stream = zip.CreateEntry(name,CompressionLevel.Fastest).Open();
                    using var x = XmlWriter.Create(stream,new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
                    x.WriteStartElement("worksheet",Ns);
                    x.WriteStartElement("sheetViews"); x.WriteStartElement("sheetView"); x.WriteAttributeString("workbookViewId","0");
                    x.WriteStartElement("pane"); x.WriteAttributeString("ySplit","1"); x.WriteAttributeString("topLeftCell","A2"); x.WriteAttributeString("state","frozen"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
                    x.WriteStartElement("cols");
                    for(int j=0;j<widths.Length;j++){x.WriteStartElement("col");x.WriteAttributeString("min",(j+1).ToString(Inv));x.WriteAttributeString("max",(j+1).ToString(Inv));x.WriteAttributeString("width",widths[j].ToString(Inv));x.WriteAttributeString("customWidth","1");x.WriteEndElement();}
                    x.WriteEndElement(); x.WriteStartElement("sheetData"); int row=0;
                    foreach(var values in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); row++; x.WriteStartElement("row");x.WriteAttributeString("r",row.ToString(Inv));
                        bool methodSheet=name.EndsWith("sheet2.xml",StringComparison.Ordinal);
                        if(methodSheet&&row>1)
                        {
                            int length=values.OfType<string>().Select(s=>s.Length).DefaultIfEmpty(0).Max();
                            x.WriteAttributeString("ht",(15*Math.Max(1,(int)Math.Ceiling(length/115.0))).ToString(Inv));x.WriteAttributeString("customHeight","1");
                        }
                        for(int j=0;j<values.Length;j++)
                        {
                            if(values[j] == null) continue;
                            x.WriteStartElement("c");x.WriteAttributeString("r",$"{(char)('A'+j)}{row}");
                            x.WriteAttributeString("s",row==1?"1":values[j] is double?"2":methodSheet?"3":"0");
                            if(values[j] is double d){if(!double.IsFinite(d))throw new InvalidDataException("Nonfinite result.");x.WriteElementString("v",d.ToString("R",Inv));}
                            else{x.WriteAttributeString("t","inlineStr");x.WriteStartElement("is");x.WriteElementString("t",Convert.ToString(values[j],Inv));x.WriteEndElement();}
                            x.WriteEndElement();
                        }
                        x.WriteEndElement();
                    }
                    x.WriteEndElement();x.WriteEndElement();
                }
                IEnumerable<object?[]> Data()
                {
                    if(positions != null)
                    {
                        yield return ["Timestamp (local, UTC offset)","Solar zenith (degrees)","Solar azimuth (degrees)"];
                        foreach(var row in positions)yield return [row.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",Inv),row.Position.ZenithDegrees,row.Position.AzimuthDegrees];
                        yield break;
                    }
                    yield return ["Timestamp (local, UTC offset)","Azimuth (degrees)","Zenith (degrees)","Direct shading factor","Loss lower bound","Loss upper bound","Direct coverage","Status"];
                    foreach(var row in result!.Rows)yield return [row.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",Inv),row.SolarPosition?.AzimuthDegrees,row.SolarPosition?.ZenithDegrees,row.Direct.ShadingFactor,row.Direct.ShadingLowerBound,row.Direct.ShadingUpperBound,row.Direct.Coverage,row.Status];
                }
                Sheet("xl/worksheets/sheet1.xml",Data(),positions==null?[37,23,23,25,23,23,23,34]:[37,28,28]);
                if(positions!=null)Sheet("xl/worksheets/sheet2.xml",[
                    ["Parameter","Value"],["Latitude (degrees)",site.LatitudeDegrees],["Longitude (degrees)",site.LongitudeDegrees],["Elevation (metres)",site.ElevationMetres],
                    ["Azimuth convention","Clockwise from true north: N=0, E=90, S=180, W=270."],
                    ["Zenith convention","0 = vertical sky; 90 = geometric horizon; greater than 90 = below horizon."],
                    ["Timestamp convention","Exact input instant and UTC offset, including night rows; no phase shift."],
                    ["Algorithm","NOAA/Meeus solar equations; WGS84 observer parallax; UTC approximates UT1; refraction disabled."],
                    ["Panel incidence","cos(i) = cos(zenith)*cos(tilt) + sin(zenith)*sin(tilt)*cos(solar azimuth - panel azimuth). Angles converted to radians."],
                    ["Horizontal direct conversion","For daylight only: DNI = BHI / cos(zenith). Do not divide at/below the horizon; interval-mean BHI requires an interval model."],
                    ["Source","https://github.com/cosinekitty/astronomy"]
                ],[43,125]);
                else Sheet("xl/worksheets/sheet2.xml",[
                    ["Parameter","Value"], ["Factor definition","0 = no loss; 1 = completely blocked. Multiply irradiance by (1 - factor)."],
                    ["Missing coverage","Default: uncovered directions are blocked, for both direct and diffuse loss. IncompleteCoverage status retains the coverage diagnostic."],
                    ["Blank factor","Explicit ReportUnknown policy or positive direct irradiance below horizon; see status and loss bounds. Do not treat blank as zero."],
                    ["Zero-direct rows","Skipped; neutral factor 0, coverage N/A (stored 0). No solar position computed."],
                    ["Diffuse shading factor",result!.Diffuse.ShadingFactor],["Diffuse loss lower bound",result.Diffuse.ShadingLowerBound],
                    ["Diffuse loss upper bound",result.Diffuse.ShadingUpperBound],["Diffuse coverage",result.Diffuse.Coverage],
                    ["Diffuse model","Constant isotropic sky; horizontal plane; cosine-weighted solid angle."],
                    ["Latitude (degrees)",site.LatitudeDegrees],["Longitude (degrees)",site.LongitudeDegrees],["Elevation (metres)",site.ElevationMetres],
                    ["Image top azimuth (degrees)",pose.ImageTopAzimuthDegrees],["Camera tilt (degrees)",pose.TiltDegrees],["Camera roll (degrees)",pose.RollDegrees],
                    ["Solar angular radius (degrees)",result.SolarAngularRadiusDegrees],["Disk samples",(double)result.DiskSamples],
                    ["Window start relative to label (min)",result.Sampling.StartOffsetMinutes],["Window duration (min)",result.Sampling.DurationMinutes],["Time samples",(double)result.Sampling.Samples],
                    ["Temporal model","Constant DNI within each interval; horizontal cosine-weighted averaging. No measured sub-hour irradiance available."],
                    ["Refraction","Disabled (geometric horizon), matching original Astropy defaults."],["Provenance",provenance]
                ],[43,125]);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temp,path,true);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
}

