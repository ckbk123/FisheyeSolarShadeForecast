using SolarShade.Irradiance;
using Xunit;

public class IntervalDatasetTests
{
    static readonly DateTimeOffset Noon = new(2025, 5, 15, 5, 0, 0, TimeSpan.Zero);
    [Theory][InlineData(15)][InlineData(60)][InlineData(10)]
    public void NativeWorkbookOverridesLegacyImportFallbackAndPreservesSourceBounds(int minutes)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".xlsx");
        try
        {
            var data = IrradianceDatasets.FromSamples([new(Noon, 500.123456789, 100.987654321),new(Noon.AddMinutes(minutes), 10, 20)],
                TimeSpan.FromMinutes(minutes), TimestampLabel.End, "fixture", "UTC", "interval end") with { Latitude=10.8, Longitude=106.7 };
            data = IrradianceDatasets.Select(data, Noon.AddMinutes(-minutes/2.0), Noon.AddMinutes(minutes));
            var exported = IrradianceDatasetFiles.Export(data, path);
            var imported = IrradianceDatasetFiles.Import(path, TimeZoneInfo.Utc, TimeSpan.FromHours(1), TimestampLabel.Start);
            Assert.Equal(data.Intervals, imported.Intervals); Assert.Equal(data.NativeCadence, imported.NativeCadence);
            Assert.Equal(data.Latitude, imported.Latitude); Assert.Equal(data.Longitude, imported.Longitude);
            Assert.Equal(Path.GetFullPath(path), exported.OutputPath);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void EndLabelsSelectRepresentedIntervalsAndMissingQuarterHourRemainsGap()
    {
        var data = IrradianceDatasets.FromSamples([new(Noon,1,1),new(Noon.AddMinutes(15),2,2),new(Noon.AddMinutes(45),3,3)],
            TimeSpan.FromMinutes(15), TimestampLabel.End, "fixture","UTC", "end");
        Assert.Equal(Noon.AddMinutes(15), Assert.Single(IrradianceDatasets.Select(data, Noon, Noon.AddMinutes(15)).Intervals).Timestamp);
        var error=Assert.Throws<InvalidDataException>(()=>IrradianceDatasets.Select(data,Noon,Noon.AddMinutes(45)));
        Assert.Contains(Noon.AddMinutes(15).ToString("O"),error.Message);
        Assert.Equal(TimeSpan.FromMinutes(15),data.Intervals[2].SourceEnd-data.Intervals[2].SourceStart);
    }
    [Fact]
    public void ExplicitVariableIntervalsPreserveTheirDurations()
    {
        var data=new IrradianceDataset([new("a",Noon,Noon,Noon.AddMinutes(10),Noon,Noon.AddMinutes(10),0,100),
            new("b",Noon.AddMinutes(10),Noon.AddMinutes(10),Noon.AddMinutes(40),Noon.AddMinutes(10),Noon.AddMinutes(40),0,100)],"variable","UTC","explicit",TimestampLabel.Explicit,null);
        var selected=IrradianceDatasets.Select(data,Noon.AddMinutes(5),Noon.AddMinutes(35));
        Assert.Equal(new[]{5.0/60,25.0/60},selected.Intervals.Select(r=>r.Hours));
        Assert.Equal(data.Intervals[1].SourceEnd,selected.Intervals[1].SourceEnd);
    }
    [Fact]
    public void AmbiguousLocalTimeRequiresOffsetAndDstDatesUseActualDuration()
    {
        var eastern=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Assert.Throws<InvalidDataException>(()=>IrradianceDatasetFiles.ParseTime("02.11.2025 01:30",eastern));
        Assert.NotEqual(IrradianceDatasetFiles.ParseTime("2025-11-02T01:30:00-04:00",eastern),IrradianceDatasetFiles.ParseTime("2025-11-02T01:30:00-05:00",eastern));
        var paris=TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        Assert.Equal(23,(IrradianceDatasets.Boundary(new(2023,3,27),paris)-IrradianceDatasets.Boundary(new(2023,3,26),paris)).TotalHours);
        Assert.Equal(Noon,IrradianceDatasetFiles.ParseTime("15.05.2025 12:00",TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")));
    }
    [Fact]
    public void TiltedWorkbooksAndNonMeanValuesAreRejected()
    {
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            ScientificWorkbook.Write(path,"Panel",["Timestamp","Direct on panel (W/m²)","Diffuse"],[new object?[]{Noon,500d,100d}],"fixture");
            Assert.Throws<InvalidDataException>(()=>IrradianceDatasetFiles.Import(path,TimeZoneInfo.Utc,TimeSpan.FromMinutes(15),TimestampLabel.Start));
            var data=IrradianceDatasets.FromSamples([new(Noon,1,1)],TimeSpan.FromMinutes(15),TimestampLabel.Start,"x","UTC","");
            Assert.Throws<ArgumentException>(()=>IrradianceDatasets.Validate(data with {ValueKind=IrradianceValueKind.AccumulatedEnergy}));
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public void ExportCancellationAndInvalidValuesPreserveExistingWorkbook()
    {
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            File.WriteAllText(path,"existing");
            Assert.Throws<OperationCanceledException>(()=>ScientificWorkbook.Write(path,"Data",["Value"],[new object?[]{1d}],"fixture",new CancellationToken(true)));
            Assert.Equal("existing",File.ReadAllText(path));
            Assert.Throws<ArgumentException>(()=>ScientificWorkbook.Write(path,"Data",["Value"],[new object?[]{double.NaN}],"fixture"));
            Assert.Equal("existing",File.ReadAllText(path));
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public void ResultMetadataControlsCadenceRatherThanServiceIdentity()
    {
        var result=new IrradianceResult(1,"ok",[new(Noon,1,1)],"UTC","explicit 15 minute endpoint") {NativeCadence=TimeSpan.FromMinutes(15),Label=TimestampLabel.End};
        Assert.Equal(TimeSpan.FromMinutes(15),IrradianceDatasets.FromResult(result,IrradianceService.OpenMeteo).NativeCadence);
        Assert.Throws<ArgumentException>(()=>IrradianceDatasets.FromResult(result with {NativeCadence=null},IrradianceService.OpenMeteo));
    }
    [Fact]
    public void LabelsAndDeclaredCadenceMustAgreeWithExplicitBounds()
    {
        var data=IrradianceDatasets.FromSamples([new(Noon,1,1)],TimeSpan.FromMinutes(15),TimestampLabel.Start,"fixture","UTC","");
        Assert.Throws<InvalidDataException>(()=>IrradianceDatasets.Validate(data with {Label=TimestampLabel.End}));
        Assert.Throws<ArgumentException>(()=>IrradianceDatasets.Validate(data with {Label=(TimestampLabel)99}));
        Assert.Throws<InvalidDataException>(()=>IrradianceDatasets.Validate(data with {NativeCadence=TimeSpan.FromMinutes(30)}));
    }
    [Theory][InlineData("P3","2020-01-01T00:00:00Z")][InlineData("Q3","11.0")][InlineData("R3","107.0")][InlineData("E2","2025-05-15T05:00:00")]
    public void NativeInputRejectsConflictingProvenanceAndAmbiguousBounds(string cell,string replacement)
    {
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            var data=IrradianceDatasets.FromSamples([new(Noon,1,1),new(Noon.AddMinutes(15),1,1)],TimeSpan.FromMinutes(15),TimestampLabel.Start,"fixture","UTC","") with {Latitude=10.8,Longitude=106.7};
            IrradianceDatasetFiles.Export(data,path);
            using(var zip=System.IO.Compression.ZipFile.Open(path,System.IO.Compression.ZipArchiveMode.Update))
            {
                System.Xml.Linq.XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var entry=zip.GetEntry("xl/worksheets/sheet1.xml")!;System.Xml.Linq.XDocument xml;
                using(var stream=entry.Open())xml=System.Xml.Linq.XDocument.Load(stream);
                var element=xml.Descendants(n+"c").Single(c=>(string?)c.Attribute("r")==cell);
                var value=element.Descendants(n+"t").FirstOrDefault()??element.Element(n+"v")!;value.Value=replacement;
                entry.Delete();using var output=zip.CreateEntry("xl/worksheets/sheet1.xml").Open();xml.Save(output);
            }
            Assert.Throws<InvalidDataException>(()=>IrradianceDatasetFiles.Import(path,TimeZoneInfo.Utc,TimeSpan.FromMinutes(60),TimestampLabel.End));
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public void FingerprintTracksSourceValuesAndBoundsButNotExportDestination()
    {
        var data=IrradianceDatasets.FromSamples([new(Noon,1,1)],TimeSpan.FromMinutes(15),TimestampLabel.Start,"fixture","UTC","");
        var original=IrradianceDatasets.Fingerprint(data);
        Assert.Equal(original,IrradianceDatasets.Fingerprint(data with {OutputPath="different.xlsx"}));
        Assert.NotEqual(original,IrradianceDatasets.Fingerprint(data with {Intervals=[data.Intervals[0] with {DirectHorizontal=2}]}));
        Assert.NotEqual(original,IrradianceDatasets.Fingerprint(IrradianceDatasets.Select(data,Noon.AddMinutes(1),Noon.AddMinutes(15))));
    }
}
