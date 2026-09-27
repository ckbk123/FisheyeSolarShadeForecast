using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using OpenCvSharp;
using SolarShade.Core.Models;
using SolarShade.Shading;
using Xunit;
using Xunit.Abstractions;

public sealed class CardinalDirectionOverlayTests(ITestOutputHelper output)
{
    private static CalibrationResult Profile(double angle = 66) => new(2,
        CameraModelKind.OmniCalibIncidentAnglePolynomial, 501, 501, [250,250], [0, 500/Math.PI],
        angle, 250, null, 0, [], DateTimeOffset.UnixEpoch, "Synthetic equidistant");

    [Fact]
    public void DefaultUpwardCameraMatchesAnalyticalCardinalsWithoutInventingHorizon()
    {
        var result = CardinalDirectionOverlayGenerator.Generate(new(Profile()));
        Assert.Equal(new[]{"N","E","S","W"},result.Markers.Select(m=>m.Name));
        double radius = 250*66/90d;
        (double X,double Y)[] expected = [(250,250-radius),(250-radius,250),(250,250+radius),(250+radius,250)];
        for(int i=0;i<4;i++)
        {
            var m=result.Markers[i]; Assert.True(m.Visible);
            Assert.Equal(66,m.BoundaryZenithDegrees!.Value,8);
            Assert.Equal(expected[i].X,m.BoundaryX!.Value,8); Assert.Equal(expected[i].Y,m.BoundaryY!.Value,8);
            Assert.Contains("coverage",m.Status);Assert.NotNull(m.LabelX);
        }
    }

    [Fact]
    public void HeadingAndRollRotateSharedGeometryRatherThanSwappingEastAndWest()
    {
        var heading=CardinalDirectionOverlayGenerator.Generate(new(Profile(),CameraPose.FromImageBottom(270)));
        var north=heading.Markers.Single(m=>m.Name=="N");
        Assert.True(north.BoundaryX>250);Assert.Equal(250,north.BoundaryY!.Value,8);
        var pose=new CameraPose(0,0,90);
        var rolled=CardinalDirectionOverlayGenerator.Generate(new(Profile(),pose));
        var east=rolled.Markers.Single(m=>m.Name=="E");
        Assert.True(east.BoundaryY>250);Assert.Equal(250,east.BoundaryX!.Value,8);Assert.Equal(pose,rolled.Pose);
    }

    [Theory]
    [InlineData(0,30,0)]
    [InlineData(52,40,-73)]
    [InlineData(237,80,33)]
    public void TiltedMarkersAndTicksUseExactMeridiansAndActualCoverage(double heading,double tilt,double roll)
    {
        var projection=new CalibratedSkyProjection(Profile(),new(heading,tilt,roll),new(265,242,205));
        var result=CardinalDirectionOverlayGenerator.Generate(projection);
        Assert.True(result.HasMarkers);
        foreach(var m in result.Markers.Where(m=>m.Visible))
        {
            Assert.True(projection.TryProject(Direction.FromAngles(m.AzimuthDegrees,m.BoundaryZenithDegrees!.Value),out double x,out double y));
            Assert.Equal(x,m.BoundaryX);Assert.Equal(y,m.BoundaryY);
            if(m.BoundaryZenithDegrees<89.99999)
                Assert.False(projection.TryProject(Direction.FromAngles(m.AzimuthDegrees,m.BoundaryZenithDegrees.Value+.000001),out _,out _));
            Assert.All(m.TickPoints,p=>{Assert.InRange(p.X,0,500);Assert.InRange(p.Y,0,500);Assert.True((p.X-265)*(p.X-265)+(p.Y-242)*(p.Y-242)<=205*205+1e-8);});
        }
    }

    [Fact]
    public void HorizonEndpointRemainsHorizonWhenWideLensCoversIt()
    {
        var result=CardinalDirectionOverlayGenerator.Generate(new(Profile(100) with {ImageCircleRadiusPixels=280}));
        Assert.All(result.Markers,m=>{Assert.True(m.Visible);Assert.Equal(90,m.BoundaryZenithDegrees);Assert.Contains("Horizon",m.Status);});
    }

    [Fact]
    public void OffsetSmallDiskCanMakeOnlyOneMeridianAvailable()
    {
        var result=CardinalDirectionOverlayGenerator.Generate(new(Profile(),imageDisk:new(280,250,10)));
        Assert.Equal("W",Assert.Single(result.Markers,m=>m.Visible).Name);
        Assert.All(result.Markers.Where(m=>!m.Visible),m=>{Assert.Null(m.BoundaryX);Assert.Empty(m.TickPoints);});
    }

    [Fact]
    public void RectangleClippingDoesNotMoveScientificEndpointToClampedLabel()
    {
        var profile=Profile() with {ImageWidth=100,ImageHeight=80,PrincipalPoint=[50,40],IncidentAngleToRadiusPolynomial=[0,100]};
        var result=CardinalDirectionOverlayGenerator.Generate(new(profile));
        var west=result.Markers.Single(m=>m.Name=="W");
        Assert.Equal(99,west.BoundaryX!.Value,8); Assert.True(west.LabelX<west.BoundaryX);
        Assert.Equal(40,west.BoundaryY!.Value,8);
    }

    [Fact]
    public void DownwardNarrowCameraProducesTransparentUnavailableResult()
    {
        var result=CardinalDirectionOverlayGenerator.Generate(new(Profile(),new(0,180)));
        Assert.False(result.HasMarkers);
        using var png=Cv2.ImDecode(result.Png,ImreadModes.Unchanged);using var alpha=new Mat();
        Cv2.ExtractChannel(png,alpha,3);Assert.Equal(0,Cv2.CountNonZero(alpha));
    }

    [Fact]
    public void NativePngHasStraightMagentaAndBoundedTransparency()
    {
        var result=CardinalDirectionOverlayGenerator.Generate(new(Profile()));
        using var png=Cv2.ImDecode(result.Png,ImreadModes.Unchanged);
        Assert.Equal(501,png.Width);Assert.Equal(501,png.Height);Assert.Equal(4,png.Channels());
        int colored=0;byte maximum=0;int width=png.Width,height=png.Height;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            var p=png.At<Vec4b>(y,x);maximum=Math.Max(maximum,p.Item3);
            if(p.Item3==0)continue;colored++;Assert.Equal(180,p.Item0);Assert.Equal(0,p.Item1);Assert.Equal(255,p.Item2);
        }
        Assert.True(colored>100);Assert.Equal((byte)217,maximum);Assert.Equal(0,png.At<Vec4b>(250,250).Item3);
    }

    [Fact]
    public void ExportContainsExactPngFourRowsAndReproducibleProjectionMetadata()
    {
        string directory=Path.Combine(Path.GetTempPath(),"cardinal-overlay-"+Guid.NewGuid().ToString("N"));
        try
        {
            var result=CardinalDirectionOverlayGenerator.Generate(new(Profile(),new(42,12,3),new(246,251,240)));
            var paths=CardinalDirectionOverlayExporter.Export(result,directory);Assert.Equal(3,paths.Count);
            Assert.Equal(result.Png,File.ReadAllBytes(paths[0]));
            using var zip=ZipFile.OpenRead(paths[1]);using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();var xml=XDocument.Load(stream);
            XNamespace n="http://schemas.openxmlformats.org/spreadsheetml/2006/main";Assert.Equal(5,xml.Descendants(n+"row").Count());
            using var json=JsonDocument.Parse(File.ReadAllBytes(paths[2]));var root=json.RootElement;
            Assert.Equal(42,root.GetProperty("Pose").GetProperty("ImageTopAzimuthDegrees").GetDouble());
            Assert.Equal(246,root.GetProperty("Projection").GetProperty("ImageDisk").GetProperty("CenterX").GetDouble());
            Assert.Equal(2,root.GetProperty("Projection").GetProperty("IncidentAngleToRadiusPolynomial").GetArrayLength());
        }
        finally {if(Directory.Exists(directory)){foreach(var file in Directory.GetFiles(directory))File.Delete(file);Directory.Delete(directory);}}
    }

    [Fact]
    public void CancellationAndInvalidOpacityAreRejected()
    {
        var projection=new CalibratedSkyProjection(Profile());
        Assert.Throws<OperationCanceledException>(()=>CardinalDirectionOverlayGenerator.Generate(projection,ct:new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>CardinalDirectionOverlayGenerator.Generate(projection,new(){Opacity=double.NaN}));
    }

    [Fact]
    public void NativePortraitBenchmarkReportsActualGenerationCosts()
    {
        var projection=new CalibratedSkyProjection(Profile() with {ImageWidth=3000,ImageHeight=4000,PrincipalPoint=[1500,2000],IncidentAngleToRadiusPolynomial=[0,1000],ImageCircleRadiusPixels=1450},new(25,12,8));
        var result=CardinalDirectionOverlayGenerator.Generate(projection);
        Assert.Equal(4,result.Markers.Count(m=>m.Visible));
        output.WriteLine(JsonSerializer.Serialize(result.Timings));
        // Timing is measured and reported, not a machine-load-dependent test failure threshold.
    }
}
