using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using SolarShade.Shading;

internal static class AnnualBenchmark
{
    public static void Run(string output,string template,string mask,string calibration,SolarSite site,CameraPose pose,ImageDisk disk)
    {
        var path=Path.Combine(output,"synthetic-year-input.xlsx");
        var solar=new SolarPositionModule(site);var start=DateTimeOffset.Parse("2025-01-01T00:00:00+07:00",CultureInfo.InvariantCulture);
        // Deterministic synthetic annual load, NOT downloaded weather or an energy forecast.
        using(var source=ZipFile.OpenRead(template))using(var file=File.Create(path))using(var zip=new ZipArchive(file,ZipArchiveMode.Create))
        {
            foreach(var entry in source.Entries)
            {
                using var stream=zip.CreateEntry(entry.FullName,CompressionLevel.Fastest).Open();
                if(entry.FullName=="docProps/core.xml")
                {
                    using var text=new StreamWriter(stream,Encoding.UTF8);
                    text.Write($"<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description>SYNTHETIC annual performance load, not weather. Requested latitude {site.LatitudeDegrees.ToString(CultureInfo.InvariantCulture)}, longitude {site.LongitudeDegrees.ToString(CultureInfo.InvariantCulture)}, interval end.</dc:description></cp:coreProperties>");
                }
                else if(entry.FullName=="xl/worksheets/sheet1.xml")
                {
                    using var xml=XmlWriter.Create(stream,new XmlWriterSettings{Encoding=new UTF8Encoding(false)});
                    xml.WriteStartElement("worksheet","http://schemas.openxmlformats.org/spreadsheetml/2006/main");xml.WriteStartElement("sheetData");
                    void Cell(string reference,object value)
                    {
                        xml.WriteStartElement("c");xml.WriteAttributeString("r",reference);
                        if(value is string s){xml.WriteAttributeString("t","inlineStr");xml.WriteStartElement("is");xml.WriteElementString("t",s);xml.WriteEndElement();}
                        else xml.WriteElementString("v",Convert.ToString(value,CultureInfo.InvariantCulture));xml.WriteEndElement();
                    }
                    xml.WriteStartElement("row");xml.WriteAttributeString("r","1");Cell("A1","Timestamp (local, UTC offset)");Cell("B1","Direct horizontal (W/m²)");Cell("C1","Diffuse horizontal (W/m²)");xml.WriteEndElement();
                    for(int i=0;i<8760;i++)
                    {
                        var t=start.AddHours(i);var up=solar.Calculate(t.AddMinutes(-30)).Direction.Up;
                        var row=(i+2).ToString(CultureInfo.InvariantCulture);xml.WriteStartElement("row");xml.WriteAttributeString("r",row);
                        Cell("A"+row,t.ToString("yyyy-MM-dd'T'HH:mm:sszzz",CultureInfo.InvariantCulture));Cell("B"+row,600*Math.Max(0,up));Cell("C"+row,up>0?100.0:0.0);xml.WriteEndElement();
                    }
                    xml.WriteEndElement();xml.WriteEndElement();
                }
                else {using var input=entry.Open();input.CopyTo(stream);}
            }
        }
        var runs=new List<object>();
        for(int i=0;i<5;i++)
        {
            var result=ShadingFilePipeline.Compute(path,mask,calibration,66.43,site,Path.Combine(output,$"synthetic-year-shading-{i}.xlsx"),TimeSampling.PrecedingHour(),pose,new(),disk);
            runs.Add(new{result.TotalMilliseconds,result.InputMilliseconds,result.PreparationMilliseconds,result.SolarMilliseconds,result.ShadingMilliseconds,result.OutputMilliseconds,Rows=result.Result.Rows.Count});
        }
        File.WriteAllText(Path.Combine(output,"annual-benchmark.json"),JsonSerializer.Serialize(new{Description="8760 distinct timestamps, synthetic daylight direct flux; file-to-file with both output workbooks; no measured annual weather claim.",Runs=runs},new JsonSerializerOptions{WriteIndented=true}));
    }
}
