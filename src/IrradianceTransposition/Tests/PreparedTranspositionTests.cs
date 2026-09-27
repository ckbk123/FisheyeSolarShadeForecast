using System.IO.Compression;
using System.Xml.Linq;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using Xunit;

public class PreparedTranspositionTests
{
    static readonly DateTimeOffset Time=new(2025,5,15,5,0,0,TimeSpan.Zero);
    static SolarTimeline Timeline(IrradianceDataset data,Func<DateTimeOffset,double> zenith,int count=2)
    {
        SolarGeometrySample Sample(DateTimeOffset t,double w)=>new(t,zenith(t),zenith(t),180,w);
        SolarGeometrySample[] Samples(DateTimeOffset start,DateTimeOffset end)=>Enumerable.Range(0,count).Select(i=>Sample(start.AddTicks((long)((i+.5)*(end-start).Ticks/count)),1.0/count)).ToArray();
        return new(data,data.Intervals.Select(r=>new SolarIntervalGeometry(r,Sample(r.Timestamp,1),Samples(r.SourceStart,r.SourceEnd),Samples(r.Start,r.End))).ToArray(),"fixture apparent angles");
    }
    [Theory][InlineData(15)][InlineData(60)][InlineData(10)]
    public void SourceCadenceAndClippedEnergyArePreserved(int minutes)
    {
        var data=IrradianceDatasets.FromSamples([new(Time,0,100),new(Time.AddMinutes(minutes),0,100)],TimeSpan.FromMinutes(minutes),TimestampLabel.Start,"fixture","UTC","");
        var selected=IrradianceDatasets.Select(data,Time.AddMinutes(minutes/2.0),Time.AddMinutes(2*minutes));
        var result=TranspositionModule.ComputePrepared(Timeline(selected,_=>40),new(0,180));
        Assert.Equal(2,result.Rows.Count); Assert.Equal(selected.Intervals,result.Rows.Select(r=>r.Interval));
        Assert.Equal(100*minutes*1.5/60/1000,result.BeforeEnergy,12);
    }
    [Fact]
    public void ClippedDniInferenceUsesWholeSourceSunrise()
    {
        var data=IrradianceDatasets.FromSamples([new(Time,200,100)],TimeSpan.FromHours(1),TimestampLabel.Start,"fixture","UTC","");
        data=IrradianceDatasets.Select(data,Time.AddMinutes(30),Time.AddHours(1));
        var result=TranspositionModule.ComputePrepared(Timeline(data,t=>t<Time.AddMinutes(30)?100:60),new(60,180),DiffuseModel.Isotropic);
        Assert.Equal(800,result.Rows[0].EffectiveDni,10); Assert.Equal(800,result.Rows[0].Direct,10);
        Assert.Equal(.4375,result.BeforeEnergy,10);
    }
    [Theory][InlineData(30)][InlineData(80)]
    public void PreparedAndEstablishedKernelAgreeForWholeIntervals(double tilt)
    {
        IrradianceSample[] input=[new(Time,400,100)];
        var data=IrradianceDatasets.FromSamples(input,TimeSpan.FromMinutes(15),TimestampLabel.Start,"fixture","UTC","");
        var prepared=TranspositionModule.ComputePrepared(Timeline(data,_=>60),new(tilt,180));
        var old=TranspositionModule.Compute(input,new(tilt,180),_=>new(60,180),new(0,15,2),DiffuseModel.HayDavies);
        Assert.True(old.Succeeded,old.Message); Assert.Equal(old.Samples[0].Total,prepared.Rows[0].Total,10);
        Assert.All(prepared.Rows[0].Samples,s=>Assert.Equal(s.SkyDiffuse,s.IsotropicDiffuse+s.CircumsolarDiffuse));
    }
    [Fact]
    public void ExportIsExactReturnedResultAndKeepsSubstepsSeparate()
    {
        var data=IrradianceDatasets.FromSamples([new(Time,0,100)],TimeSpan.FromMinutes(15),TimestampLabel.Start,"fixture","UTC","");
        var result=TranspositionModule.ComputePrepared(Timeline(data,_=>40),new(30,180));
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            TranspositionModule.ExportPrepared(result,path);
            using var zip=ZipFile.OpenRead(path); XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var xml=XDocument.Load(stream);
            Assert.Equal(2,xml.Descendants(n+"row").Count());
            var value=xml.Descendants(n+"c").Single(c=>(string?)c.Attribute("r")=="C2").Element(n+"v")!.Value;
            Assert.Equal(result.Rows[0].SkyDiffuse,double.Parse(value,System.Globalization.CultureInfo.InvariantCulture));
            using var detail=zip.GetEntry("xl/worksheets/sheet2.xml")!.Open(); Assert.Equal(3,XDocument.Load(detail).Descendants(n+"row").Count());
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public void LargeDiagnosticsSplitWithoutLosingBoundarySamples()
    {
        var data=IrradianceDatasets.FromSamples([new(Time,0,100)],TimeSpan.FromHours(1),TimestampLabel.Start,"fixture","UTC","");
        var timeline=Timeline(data,_=>40);
        var samples=Enumerable.Range(0,100001).Select(i=>new TransposedSubstep(new(Time.AddTicks(i),40,40,180,1.0/100001),i,100,0,TranspositionFlags.None)).ToArray();
        var result=new PreparedTranspositionResult(timeline,new(30,180),DiffuseModel.HayDavies,[new(data.Intervals[0],samples,50000,100,0,0,TranspositionFlags.None)]);
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            TranspositionModule.ExportPrepared(result,path);
            using var zip=ZipFile.OpenRead(path); XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var first=zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();
            // Stream large sheet to avoid a test-only object graph for a million cells.
            using(var reader=System.Xml.XmlReader.Create(first))
            { int rows=0;while(reader.Read())if(reader.NodeType==System.Xml.XmlNodeType.Element&&reader.LocalName=="row")rows++;Assert.Equal(100001,rows); }
            using var last=zip.GetEntry("xl/worksheets/sheet3.xml")!.Open();var xml=XDocument.Load(last);
            Assert.Equal(2,xml.Descendants(n+"row").Count());
            Assert.Equal("100000",xml.Descendants(n+"c").Single(c=>(string?)c.Attribute("r")=="G2").Element(n+"v")!.Value);
        }
        finally{File.Delete(path);}
    }
}
