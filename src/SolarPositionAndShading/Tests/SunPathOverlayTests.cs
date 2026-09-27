using System.IO.Compression;
using System.Xml.Linq;
using OpenCvSharp;
using SolarShade.Core.Models;
using SolarShade.Irradiance;
using SolarShade.Shading;
using Xunit;

public class SunPathOverlayTests
{
    static readonly DateTimeOffset Noon = new(2025, 5, 15, 12, 0, 0, TimeSpan.Zero);
    static CalibrationResult Profile(double coverage = 90) => new(2, CameraModelKind.OmniCalibIncidentAnglePolynomial,
        201, 241, [100, 120], [0, 180 / Math.PI], coverage, 90, null, 0, [], Noon, "Synthetic equidistant");
    static SolarTimeline Timeline(DateTimeOffset start, int minutes, params (double Zenith, double Azimuth)[] angles)
    {
        var data = IrradianceDatasets.FromSamples([new(start, 0, 100)], TimeSpan.FromMinutes(minutes), TimestampLabel.Start,"fixture","UTC","start");
        var samples = angles.Select((p,i) => new SolarGeometrySample(start.AddMinutes((i+.5)*minutes/angles.Length),p.Zenith,p.Zenith,p.Azimuth,1.0/angles.Length)).ToArray();
        return new(data,[new(data.Intervals[0],new(start,120,120,0,1),samples,samples)],"Prepared fixture apparent angles; no solar recomputation");
    }
    [Fact]
    public void UsesOnlySelectedSamplesAndMatchesSharedProjectionExactly()
    {
        var full = Timeline(Noon,60,(20,30),(40,80),(45,120),(60,210));
        var selected = IrradianceDatasets.Select(full.Dataset,Noon.AddMinutes(15),Noon.AddMinutes(45));
        SolarGeometrySample[] samples = [new(Noon.AddMinutes(22.5),40,40,80,.5),new(Noon.AddMinutes(37.5),45,45,120,.5)];
        var timeline = new SolarTimeline(selected,[new(selected.Intervals[0],full.Intervals[0].AtLabel,full.Intervals[0].SourceSamples,samples)],full.Convention);
        var projection = new CalibratedSkyProjection(Profile());
        var result = SunPathOverlayGenerator.Generate(timeline,projection,TimeZoneInfo.Utc);
        Assert.Equal(2,result.SelectedSampleCount);Assert.Equal(2,result.ProjectedSampleCount);Assert.Equal(1,result.TrackCount);
        Assert.Equal(samples.Select(s=>s.Timestamp),result.Vertices.Select(v=>v.Timestamp));
        foreach(var vertex in result.Vertices)
        {
            var sample = samples[vertex.SampleIndex];
            Assert.True(projection.TryProject(Direction.FromAngles(sample.AzimuthDegrees,sample.ApparentZenithDegrees),out var x,out var y));
            Assert.Equal(x,vertex.PixelX);Assert.Equal(y,vertex.PixelY);
        }
    }
    [Fact]
    public void NativeTransparentYellowPngHasConstantColorAndBoundedAlpha()
    {
        var result=SunPathOverlayGenerator.Generate(Timeline(Noon,60,(45,90),(45,130)),new(Profile()),TimeZoneInfo.Utc);
        using var png=Cv2.ImDecode(result.Png,ImreadModes.Unchanged);
        Assert.Equal(201,png.Width);Assert.Equal(241,png.Height);Assert.Equal(4,png.Channels());
        int visible=0;byte maximum=0;int width=png.Width,height=png.Height;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            var p=png.At<Vec4b>(y,x);maximum=Math.Max(maximum,p.Item3);
            if(p.Item3>0){visible++;Assert.Equal(0,p.Item0);Assert.Equal(210,p.Item1);Assert.Equal(255,p.Item2);}
        }
        Assert.True(visible>0);Assert.Equal((byte)128,maximum);Assert.Equal(0,png.At<Vec4b>(0,0).Item3);
    }
    [Fact]
    public void NightAndUnprojectableSamplesBreakTracks()
    {
        var result=SunPathOverlayGenerator.Generate(Timeline(Noon,60,(40,60),(80,80),(40,100),(100,120),(40,140)),new(Profile(60)),TimeZoneInfo.Utc);
        Assert.Equal(3,result.TrackCount);Assert.Equal(3,result.Vertices.Count);
        Assert.Equal(1,result.NightSampleCount);Assert.Equal(1,result.UnprojectedSampleCount);
    }
    [Fact]
    public void DailyGroupingUsesRequestedDisplayZoneRatherThanSourceZone()
    {
        var timeline=Timeline(new(2025,5,15,16,0,0,TimeSpan.Zero),120,(40,80),(40,100));
        var utc=SunPathOverlayGenerator.Generate(timeline,new(Profile()),TimeZoneInfo.Utc);
        var local=SunPathOverlayGenerator.Generate(timeline,new(Profile()),TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"));
        Assert.Equal(1,utc.TrackCount);Assert.Equal(2,local.TrackCount);
        Assert.Equal(new[]{new DateOnly(2025,5,15),new DateOnly(2025,5,16)},local.Vertices.Select(v=>v.LocalDate));
    }
    [Fact]
    public void SourceGapsAndMissingSolarSamplesDoNotGetConnectingLines()
    {
        var first=Timeline(Noon,60,(40,60),(40,70));var last=Timeline(Noon.AddHours(2),60,(40,100),(40,110));
        var data=first.Dataset with {Intervals=[first.Dataset.Intervals[0],last.Dataset.Intervals[0]]};
        var result=SunPathOverlayGenerator.Generate(new(data,[first.Intervals[0],last.Intervals[0]],first.Convention),new(Profile()),TimeZoneInfo.Utc);
        Assert.Equal(2,result.TrackCount);
        var interval=Timeline(Noon,60,(40,60),(40,70),(40,100),(40,110));
        int[] minutes=[1,5,50,55];var samples=interval.Intervals[0].Samples.Select((s,i)=>s with {Timestamp=Noon.AddMinutes(minutes[i])}).ToArray();
        var modified=interval with {Intervals=[interval.Intervals[0] with {Samples=samples}]};
        Assert.Equal(2,SunPathOverlayGenerator.Generate(modified,new(Profile()),TimeZoneInfo.Utc).TrackCount);
    }
    [Fact]
    public void SimplificationRetainsEndpointsAndRespectsNativePixelErrorBound()
    {
        const double tolerance=.35;
        var angles=Enumerable.Range(0,1000).Select(i=>(Zenith:45+8*Math.Sin(i/100.0),Azimuth:40+i*.15)).ToArray();
        var timeline=Timeline(Noon,60,angles);var projection=new CalibratedSkyProjection(Profile());
        var result=SunPathOverlayGenerator.Generate(timeline,projection,TimeZoneInfo.Utc,new(){SimplificationTolerancePixels=tolerance});
        Assert.True(result.Vertices.Count<100);Assert.Equal(0,result.Vertices[0].SampleIndex);Assert.Equal(999,result.Vertices[^1].SampleIndex);
        for(int i=1;i<result.Vertices.Count;i++)
        {
            var a=result.Vertices[i-1];var b=result.Vertices[i];
            for(int j=a.SampleIndex;j<=b.SampleIndex;j++)
            {
                var s=timeline.Intervals[0].Samples[j];Assert.True(projection.TryProject(Direction.FromAngles(s.AzimuthDegrees,s.ApparentZenithDegrees),out var x,out var y));
                double dx=b.PixelX-a.PixelX,dy=b.PixelY-a.PixelY;
                double t=Math.Clamp(((x-a.PixelX)*dx+(y-a.PixelY)*dy)/(dx*dx+dy*dy),0,1);
                double ex=x-a.PixelX-t*dx,ey=y-a.PixelY-t*dy;
                Assert.True(ex*ex+ey*ey<=tolerance*tolerance+1e-12);
            }
        }
    }
    [Fact]
    public void CameraPoseChangesOutputAndNoDaylightProducesTransparentImage()
    {
        var timeline=Timeline(Noon,60,(45,60),(45,100));
        var original=SunPathOverlayGenerator.Generate(timeline,new(Profile()),TimeZoneInfo.Utc);
        var rotated=SunPathOverlayGenerator.Generate(timeline,new(Profile(),new(90,0,0)),TimeZoneInfo.Utc);
        Assert.False(original.Png.SequenceEqual(rotated.Png));
        var night=SunPathOverlayGenerator.Generate(Timeline(Noon,60,(100,60),(120,100)),new(Profile()),TimeZoneInfo.Utc);
        Assert.False(night.HasPaths);Assert.Equal(2,night.NightSampleCount);
        using var png=Cv2.ImDecode(night.Png,ImreadModes.Unchanged);using var alpha=new Mat();Cv2.ExtractChannel(png,alpha,3);Assert.Equal(0,Cv2.CountNonZero(alpha));
    }
    [Fact]
    public void CompactExportContainsOnlyRenderedVertexReferencesAndExactPng()
    {
        string directory=Path.Combine(Path.GetTempPath(),"overlay-"+Guid.NewGuid().ToString("N"));
        try
        {
            var result=SunPathOverlayGenerator.Generate(Timeline(Noon,60,(40,60),(40,70),(40,80)),new(Profile()),TimeZoneInfo.Utc);
            var paths=SunPathOverlayGenerator.Export(result,directory);Assert.Equal(3,paths.Count);
            Assert.Equal(result.Png,File.ReadAllBytes(paths[0]));
            using var zip=ZipFile.OpenRead(paths[1]);using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();var xml=XDocument.Load(stream);
            XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Equal(result.Vertices.Count+1,xml.Descendants(n+"row").Count());
            Assert.Contains(result.DatasetFingerprint,File.ReadAllText(paths[2]));
        }
        finally {if(Directory.Exists(directory)){foreach(var file in Directory.GetFiles(directory))File.Delete(file);Directory.Delete(directory);}}
    }
    [Fact]
    public void CancellationAndInvalidOptionsFailBeforePublishing()
    {
        var timeline=Timeline(Noon,60,(40,60));var projection=new CalibratedSkyProjection(Profile());
        Assert.Throws<OperationCanceledException>(()=>SunPathOverlayGenerator.Generate(timeline,projection,TimeZoneInfo.Utc,ct:new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SunPathOverlayGenerator.Generate(timeline,projection,TimeZoneInfo.Utc,new(){Opacity=2}));
    }
    [Theory][InlineData(180)][InlineData(179.95)]
    public void DownwardWideAngleCameraNeverConnectsAcrossUnprojectableRearAxis(double coverage)
    {
        var profile=Profile(coverage) with {ImageWidth=401,ImageHeight=401,PrincipalPoint=[200,200],IncidentAngleToRadiusPolynomial=[0,50],ImageCircleRadiusPixels=190};
        var timeline=Timeline(Noon,60,(.1,90),(.1,270));
        var result=SunPathOverlayGenerator.Generate(timeline,new(profile,new(0,180,0)),TimeZoneInfo.Utc);
        Assert.Equal(2,result.ProjectedSampleCount);Assert.Equal(2,result.TrackCount);
        using var image=Cv2.ImDecode(result.Png,ImreadModes.Unchanged);Assert.Equal(0,image.At<Vec4b>(200,200).Item3);
    }
    [Fact]
    public void RepeatedTracksNeverAccumulateAlphaAboveRequestedOpacity()
    {
        var first=Timeline(Noon,60,(45,90),(45,130));var second=Timeline(Noon.AddDays(1),60,(45,90),(45,130));
        var timeline=new SolarTimeline(first.Dataset with {Intervals=[first.Dataset.Intervals[0],second.Dataset.Intervals[0]]},[first.Intervals[0],second.Intervals[0]],first.Convention);
        var result=SunPathOverlayGenerator.Generate(timeline,new(Profile()),TimeZoneInfo.Utc);
        using var image=Cv2.ImDecode(result.Png,ImreadModes.Unchanged);using var alpha=new Mat();Cv2.ExtractChannel(image,alpha,3);
        Cv2.MinMaxLoc(alpha,out _,out double maximum);Assert.Equal(128,maximum);Assert.Equal(2,result.TrackCount);
    }
    [Fact]
    public void NonfiniteAnglesAreInvalidRatherThanNight()
    {
        var result=SunPathOverlayGenerator.Generate(Timeline(Noon,60,(double.PositiveInfinity,0),(181,90),(100,120)),new(Profile()),TimeZoneInfo.Utc);
        Assert.Equal(2,result.UnprojectedSampleCount);Assert.Equal(1,result.NightSampleCount);
    }
    [Fact]
    public void CachedLocalDayBoundariesHandleInvalidAndAmbiguousMidnight()
    {
        var spring=TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1,1,1,0,0,0),3,1);
        var autumn=TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1,1,1,1,0,0),10,1);
        var rule=TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2025,1,1),new DateTime(2025,12,31),TimeSpan.FromHours(1),spring,autumn);
        var zone=TimeZoneInfo.CreateCustomTimeZone("OverlayMidnightDST",TimeSpan.Zero,"Midnight test","Standard","Daylight",[rule]);
        foreach(var start in new[]{new DateTimeOffset(2025,2,28,22,0,0,TimeSpan.Zero),new DateTimeOffset(2025,9,30,22,0,0,TimeSpan.Zero)})
        {
            var timeline=Timeline(start,240,Enumerable.Range(0,16).Select(i=>(40d,60d+i)).ToArray());
            var result=SunPathOverlayGenerator.Generate(timeline,new(Profile()),zone,new(){SimplificationTolerancePixels=0});
            Assert.All(result.Vertices,v=>Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(v.Timestamp,zone).DateTime),v.LocalDate));
            Assert.Equal(2,result.TrackCount);
        }
    }
    [Fact]
    public void CompactExportSplitsLargeUnsimplifiedVertexListsWithoutDroppingBoundary()
    {
        string directory=Path.Combine(Path.GetTempPath(),"overlay-chunk-"+Guid.NewGuid().ToString("N"));
        try
        {
            var result=SunPathOverlayGenerator.Generate(Timeline(Noon,60,(40,60)),new(Profile()),TimeZoneInfo.Utc);
            result=result with {Vertices=Enumerable.Range(0,100001).Select(i=>result.Vertices[0] with {SampleIndex=i}).ToArray()};
            var paths=SunPathOverlayGenerator.Export(result,directory);
            using var zip=ZipFile.OpenRead(paths[1]);using var stream=zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();var xml=XDocument.Load(stream);
            XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Equal(2,xml.Descendants(n+"row").Count());
            Assert.Equal("100000",xml.Descendants(n+"c").Single(c=>(string?)c.Attribute("r")=="D2").Element(n+"v")!.Value);
        }
        finally{if(Directory.Exists(directory)){foreach(var file in Directory.GetFiles(directory))File.Delete(file);Directory.Delete(directory);}}
    }
}
