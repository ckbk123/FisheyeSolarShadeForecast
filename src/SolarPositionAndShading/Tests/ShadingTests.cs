using OpenCvSharp;
using SolarShade.Camera;
using SolarShade.Core.Models;
using SolarShade.Irradiance;
using SolarShade.Shading;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

public class ShadingTests
{
    private static readonly SolarSite Site=new(10.8,106.7,0);
    private static readonly DateTimeOffset Noon=DateTimeOffset.Parse("2026-05-31T12:00:00+07:00");
    private static CalibrationResult Profile(double maxAngle=90)=>new(2,CameraModelKind.OmniCalibIncidentAnglePolynomial,512,512,[255.5,255.5],[0,150],maxAngle,255,null,0,[],DateTimeOffset.UtcNow,"Synthetic equidistant test lens");
    private static SkyMask Mask(Func<int,int,byte> f)=>new(512,512,Enumerable.Range(0,512*512).Select(i=>f(i%512,i/512)).ToArray());
    private static SolarSequence Sequence(params SolarAngles[] angles)=>new(Site,TimeSampling.Instant,angles.Select((a,i)=>new SolarRow(Noon.AddHours(i),100,a,new[]{a.Direction})).ToArray());
    private static ShadingOptions Options=>new(){DiffuseSamples=8192,DiskSamples=512,MaxDegreeOfParallelism=1};

    [Fact] public void MeeusMatchesIndependentSavedAstropyCases()
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../../"));
        using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root,
            "src/SolarPositionAndShading/Validator/results/solar-astropy-validation.json")));
        var cases=document.RootElement.GetProperty("Cases");
        Assert.Equal(240,cases.GetArrayLength());
        foreach(var c in cases.EnumerateArray())
        {
            var s=c.GetProperty("Site");
            var module=new SolarPositionModule(new(s.GetProperty("LatitudeDegrees").GetDouble(),
                s.GetProperty("LongitudeDegrees").GetDouble(),s.GetProperty("ElevationMetres").GetDouble()));
            var actual=module.Calculate(DateTimeOffset.Parse(c.GetProperty("Timestamp").GetString()!)).Direction;
            var expected=Direction.FromAngles(c.GetProperty("AstropyAzimuthDegrees").GetDouble(),
                c.GetProperty("AstropyZenithDegrees").GetDouble());
            var delta=actual+(-1)*expected;
            var error=2*Math.Asin(Math.Clamp(Math.Sqrt(delta.Dot(delta))/2,0,1))*180/Math.PI;
            Assert.InRange(error,0,1.0/60); // Existing independent one-arcminute acceptance.
        }
    }

    // NOAA live table, 10.8 N / 106.7 E, refraction off, retrieved 2026-09-06.
    // The implementation additionally retains observer parallax; NOAA endpoints also use different approximations.
    [Theory]
    [InlineData("2025-05-01T06:00:00+07:00",84.91198,75.58980)]
    [InlineData("2025-05-01T12:00:00+07:00",4.95016,331.79845)]
    [InlineData("2025-05-15T18:00:00+07:00",88.92841,289.12556)]
    [InlineData("2025-05-31T12:00:00+07:00",11.35853,349.23290)]
    public void MeeusAgreesWithNoaaTable(string timestamp,double zenith,double azimuth)
    {
        var actual=new SolarPositionModule(Site).Calculate(DateTimeOffset.Parse(timestamp)).Direction;
        var delta=actual+(-1)*Direction.FromAngles(azimuth,zenith);
        Assert.InRange(Math.Sqrt(delta.Dot(delta)),0,2*Math.Sin(0.006*Math.PI/360));
    }

    [Theory][InlineData(90)][InlineData(-90)][InlineData(0)]
    public void MeeusRemainsFiniteAtPolesAndDateLimits(double latitude)
    {
        var module=new SolarPositionModule(new(latitude,180,0));
        foreach(var date in new[]{"1900-01-01T00:00:00Z","2000-02-29T12:00:00Z","2100-12-31T23:59:59Z"})
        {
            var instant=DateTimeOffset.Parse(date);var result=module.Calculate(instant);
            Assert.InRange(result.ZenithDegrees,0,180);Assert.InRange(result.AzimuthDegrees,0,359.99999999999999);
            Assert.Equal(result,module.Calculate(instant.ToOffset(TimeSpan.FromHours(-7))));
        }
        Assert.Throws<ArgumentOutOfRangeException>(()=>module.Calculate(DateTimeOffset.Parse("1899-12-31T23:59:59Z")));
    }

    [Fact] public void WhiteAndBlackArePhysicalExtremes()
    {
        foreach(byte v in new byte[]{0,255})
        {
            var engine=new SkyShadingModule(Mask((x,y)=>v),Profile(),options:Options);
            var result=engine.Evaluate(Sequence(new(0,0),new(90,45),new(220,80)));
            Assert.All(result.Rows,r=>{Assert.Equal(1-v/255.0,r.Direct.ShadingFactor!.Value,12);Assert.Equal(1,r.Direct.Coverage,12);});
            Assert.Equal(1-v/255.0,result.Diffuse.ShadingFactor!.Value,12);
        }
    }
    [Theory][InlineData(0,0,-1)][InlineData(90,-1,0)][InlineData(180,0,1)][InlineData(270,1,0)]
    public void DefaultPhoneOrientation(double a,double dx,double dy)
    {
        var e=new SkyShadingModule(Mask((x,y)=>255),Profile(),options:Options);
        Assert.True(e.TryProject(Direction.FromAngles(a,30),out var x,out var y));
        double r=150*Math.PI/6;
        Assert.Equal(255.5+dx*r,x,9);Assert.Equal(255.5+dy*r,y,9);
    }
    [Fact] public void CameraTiltAndLegacyAdapterAgreeWithExistingProjector()
    {
        foreach(var psi in new[]{0,35,180})foreach(var omega in new[]{-20,0,20})
        {
            var e=new SkyShadingModule(Mask((x,y)=>255),Profile(),CameraPose.FromLegacy(psi,omega),Options);
            var oldSite=new SiteConfiguration(0,0,0,180,0,psi,omega,new(2026,1,1),new(2026,1,2),IrradianceSource.NasaPower);
            foreach(var a in new[]{0,90,180,270})
            {
                Assert.True(e.TryProject(Direction.FromAngles(a,40),out var x,out var y));
                Assert.True(OmniCalibProjector.TryProjectSolarDirection(a,40,Profile(),oldSite,out var oldX,out var oldY));
                Assert.Equal(oldX,x,9);Assert.Equal(oldY,y,9);
            }
        }
    }
    [Fact] public void RearwardRaysDoNotFoldForward()
    {
        var e=new SkyShadingModule(Mask((x,y)=>255),Profile(),options:Options);
        Assert.False(e.TryProject(Direction.FromAngles(20,120),out _,out _));
    }
    [Fact] public void HalfDiskAtZenithIsHalfVisible()
    {
        var e=new SkyShadingModule(Mask((x,y)=>x>=256?(byte)255:(byte)0),Profile(),options:Options with{DiskSamples=4096});
        var r=e.Evaluate(Sequence(new SolarAngles(0,0))).Rows[0];
        Assert.InRange(r.Direct.ShadingFactor!.Value,0.498,0.502);
        Assert.All(e.ProjectBoundary(new(0,0)),p=>Assert.NotNull(p));
    }
    [Fact] public void UnseenSkyProducesBoundsInsteadOfRenormalizing()
    {
        var e=new SkyShadingModule(Mask((x,y)=>255),Profile(45),options:Options with{MissingCoverage=MissingCoveragePolicy.ReportUnknown});
        var r=e.Evaluate(Sequence(new SolarAngles(0,45))).Rows[0];
        Assert.Null(r.Direct.ShadingFactor);Assert.InRange(r.Direct.Coverage,0.45,0.55);
        Assert.Equal(0,r.Direct.ShadingLowerBound,10);
        Assert.InRange(e.Diffuse.Coverage,0.499,0.501);Assert.Null(e.Diffuse.ShadingFactor);
    }
    [Fact] public void DefaultBlockedPolicyAndNightInconsistencyAreDistinct()
    {
        var e=new SkyShadingModule(Mask((x,y)=>255),Profile(45),options:Options);
        var r=e.Evaluate(Sequence(new(0,60),new(0,120)));
        Assert.Equal(1,r.Rows[0].Direct.ShadingFactor);Assert.Equal("IncompleteCoverage",r.Rows[0].Status);
        Assert.Null(r.Rows[1].Direct.ShadingFactor);Assert.Equal("PositiveDirectBelowHorizon",r.Rows[1].Status);
        var partial=e.Evaluate(Sequence(new SolarAngles(0,45))).Rows[0].Direct;
        Assert.InRange(partial.ShadingFactor!.Value,0.45,0.55);
        Assert.Equal(partial.ShadingUpperBound,partial.ShadingFactor.Value);
        Assert.InRange(e.Diffuse.ShadingFactor!.Value,0.499,0.501);
    }
    [Fact] public void ZeroDirectSkipsAstronomyAndPreservesFirstAndGappedRows()
    {
        var input=new[]{new IrradianceSample(Noon,100,30),new IrradianceSample(Noon.AddHours(1),0,30),new IrradianceSample(Noon.AddDays(1),100,30)};
        var solar=new SolarPositionModule(Site).Prepare(input,TimeSampling.Instant);
        Assert.Null(solar.Rows[1].AtLabel);Assert.Empty(solar.Rows[1].IntegrationDirections);
        var r=new SkyShadingModule(Mask((x,y)=>255),Profile(),options:Options).Evaluate(solar);
        Assert.Equal("Computed",r.Rows[0].Status);Assert.Equal("SkippedZeroDirect",r.Rows[1].Status);Assert.Equal("Computed",r.Rows[2].Status);
        Assert.Equal(input.Select(s=>s.TimestampUtc),r.Rows.Select(s=>s.Timestamp));
    }
    [Fact] public void OffsetsIdentifySameInstantAndDstFoldKeepsBothRows()
    {
        var solar=new SolarPositionModule(Site);
        Assert.Equal(solar.Calculate(Noon),solar.Calculate(Noon.ToUniversalTime()));
        var one=DateTimeOffset.Parse("2025-11-02T01:00:00-04:00");var two=DateTimeOffset.Parse("2025-11-02T01:00:00-05:00");
        var r=solar.Prepare(new[]{new IrradianceSample(one,0,0),new IrradianceSample(two,0,0)},TimeSampling.Instant);
        Assert.Equal(2,r.Rows.Count);Assert.Equal(TimeSpan.FromHours(-4),r.Rows[0].Timestamp.Offset);Assert.Equal(TimeSpan.FromHours(-5),r.Rows[1].Timestamp.Offset);
        Assert.Throws<ArgumentException>(()=>solar.Prepare(new[]{new IrradianceSample(one,1,1),new IrradianceSample(one.ToUniversalTime(),1,1)},TimeSampling.Instant));
    }
    [Fact] public void IntervalEndAndStartUseCorrectSubtimestamps()
    {
        var solar=new SolarPositionModule(Site);var input=new[]{new IrradianceSample(Noon,100,30)};
        var before=solar.Prepare(input,TimeSampling.PrecedingHour(2));var after=solar.Prepare(input,TimeSampling.FollowingHour(2));
        Assert.Equal(solar.Calculate(Noon.AddMinutes(-45)).Direction,before.Rows[0].IntegrationDirections[0]);
        Assert.Equal(solar.Calculate(Noon.AddMinutes(45)).Direction,after.Rows[0].IntegrationDirections[1]);
    }
    [Fact] public void HeightAffectsTopocentricPosition()
    {
        var low=new SolarPositionModule(new(27.98,86.92,0)).Calculate(Noon).Direction;
        var high=new SolarPositionModule(new(27.98,86.92,8849)).Calculate(Noon).Direction;
        Assert.True((low+(-1)*high).Dot(low+(-1)*high)>1e-18);
    }
    [Fact] public void ShortcutMatchesFullSphericalSampling()
    {
        var mask=Mask((x,y)=>((x/37+y/41)%3==0)?(byte)0:(byte)255);
        foreach(var radius in new[]{0.25,2.5})
        {
            var o=Options with{SolarAngularRadiusDegrees=radius};
            var fast=new SkyShadingModule(mask,Profile(),new(35,12),o);
            var exact=new SkyShadingModule(mask,Profile(),new(35,12),o with{UseUniformRegionShortcut=false});
            var sequence=Sequence(Enumerable.Range(0,180).Select(i=>new SolarAngles(i*137.5%360,i%89)).ToArray());
            var a=fast.Evaluate(sequence);var b=exact.Evaluate(sequence);
            for(int i=0;i<a.Rows.Count;i++)
            {
                Assert.Equal(b.Rows[i].Direct.ShadingLowerBound,a.Rows[i].Direct.ShadingLowerBound,11);
                Assert.Equal(b.Rows[i].Direct.ShadingUpperBound,a.Rows[i].Direct.ShadingUpperBound,11);
                Assert.Equal(b.Rows[i].Direct.Coverage,a.Rows[i].Direct.Coverage,11);
            }
        }
    }
    [Fact] public void PngReaderAgreesWithOpenCvExactly()
    {
        var rng=new Random(73);
        using var mat=new Mat(157,231,MatType.CV_8UC1);
        for(int y=0;y<157;y++)for(int x=0;x<231;x++)mat.Set(y,x,rng.Next(4)==0?(byte)0:(byte)255);
        foreach(var strategy in Enumerable.Range(0,5))
        {
            Cv2.ImEncode(".png",mat,out var encoded,[new ImageEncodingParam(ImwriteFlags.PngStrategy,strategy)]);
            var decoded=SkyShadingModule.DecodeMask(encoded);
            for(int y=0;y<157;y++)for(int x=0;x<231;x++)Assert.Equal(mat.At<byte>(y,x),decoded.Pixels[y*231+x]);
            encoded[encoded.Length/2]^=1;
            Assert.Throws<InvalidDataException>(()=>SkyShadingModule.DecodeMask(encoded));
        }
    }
    [Fact] public void InvalidMaskAndNonmonotonicCalibrationFail()
    {
        Assert.Throws<ArgumentException>(()=>new SkyShadingModule(Mask((x,y)=>128),Profile(),options:Options));
        Assert.Throws<ArgumentException>(()=>new SkyShadingModule(Mask((x,y)=>255),Profile() with{IncidentAngleToRadiusPolynomial=[0,150,-200]},options:Options));
        Assert.Throws<ArgumentException>(()=>new SkyShadingModule(new(2,2,[0,0,0,0]),Profile(),options:Options));
    }
    [Fact] public void OutputRetainsEveryTimestampOffsetAndDefaultBlockedCells()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            var r=new SkyShadingModule(Mask((x,y)=>255),Profile(45),options:Options).Evaluate(Sequence(new(0,0),new(0,60)));
            ShadingWorkbook.Write(path,r,Site,new(),"Unit test");
            using var zip=ZipFile.OpenRead(path);using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var doc=XDocument.Load(stream);XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Equal(3,doc.Descendants(n+"row").Count());
            var cells=doc.Descendants(n+"c").ToDictionary(e=>(string)e.Attribute("r")!);
            Assert.Contains("+07:00",cells["A2"].Value);Assert.Equal("1",cells["D3"].Value);Assert.Equal("1",cells["F3"].Value);
        }
        finally{File.Delete(path);}
    }
    [Fact] public void StandaloneSolarWorkbookIncludesNightAndCorrectColumnOrder()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            var labels=new[]{Noon.AddHours(-12),Noon};
            var result=new SolarPositionModule(Site).ComputeToWorkbook(labels,path);
            using var zip=ZipFile.OpenRead(path);using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var doc=XDocument.Load(stream);XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var cells=doc.Descendants(n+"c").ToDictionary(e=>(string)e.Attribute("r")!);
            Assert.Contains("zenith",cells["B1"].Value);Assert.Contains("azimuth",cells["C1"].Value);
            Assert.Equal(result[0].Position.ZenithDegrees,double.Parse(cells["B2"].Value,System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(result[0].Position.ZenithDegrees>90);Assert.Contains("+07:00",cells["A2"].Value);
        }
        finally{File.Delete(path);}
    }
    [Theory][InlineData("OpenMeteo",-60)][InlineData("NasaPower",0)]
    public void RealExtractorWorkbookKeepsLabelsAndResolvesVerifiedWindow(string provider,int startMinutes)
    {
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../../"));
        var path=Path.Combine(root,$"src/IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905/{provider}_20250501_20250601_requested.xlsx");
        var input=ShadingWorkbook.ReadIrradiance(path);
        Assert.Equal(744,input.Samples.Count);Assert.Equal(TimeSpan.FromHours(7),input.Samples[0].TimestampUtc.Offset);
        Assert.Equal(startMinutes,input.SuggestedSampling!.StartOffsetMinutes);Assert.Equal(10.8,input.Latitude);Assert.Equal(106.7,input.Longitude);
    }
}
