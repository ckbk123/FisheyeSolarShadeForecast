using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenCvSharp;
using SolarShade.Shading;
using SolarShade.Irradiance;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../../"));
if(!Directory.Exists(Path.Combine(root,"src")))throw new DirectoryNotFoundException(root);
string? Arg(string key) { int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null; }
var folder=Path.Combine(root,"src/SolarPositionAndShading/Validator");
var output=Arg("--output")??Path.Combine(folder,"results");Directory.CreateDirectory(output);
var maskPath=Path.Combine(root,"src/SkyPhotoMasking/Validator/example_image-efficientnet-b5.png");
var imagePath=Path.Combine(root,"src/SkyPhotoMasking/Validator/example_image.jpg");
var calibrationPath=Path.Combine(root,"artifacts/calibration-validation/ready-to-run/calibration.yml");
var inputPath=Path.Combine(root,"src/IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905/OpenMeteo_20250501_20250601_requested.xlsx");
double Elevation=double.Parse(Arg("--elevation")??"0",CultureInfo.InvariantCulture);
var site=new SolarSite(10.8,106.7,Elevation);var pose=new CameraPose();
var disk=new ImageDisk(1521.8750635782878,2003.1250317891438,882.9166889190674);
var options=new ShadingOptions { MaxDegreeOfParallelism=int.Parse(Arg("--workers")??"0"), DiskSamples=int.Parse(Arg("--disk-samples")??"128") };
var sampling=TimeSampling.PrecedingHour(int.Parse(Arg("--time-samples")??"60"));
var jsonOptions=new JsonSerializerOptions{WriteIndented=true};

FileRun Run(string name)=>ShadingFilePipeline.Compute(inputPath,maskPath,calibrationPath,66.43,site,Path.Combine(output,name+".xlsx"),sampling,pose,options,disk);
var cold=Run("shading-month");
var timings=new List<object>();
int repeats=int.Parse(Arg("--repeats")??"10");
for(int i=0;i<repeats;i++)
{
    var r=Run("shading-month");
    timings.Add(new { r.TotalMilliseconds,r.InputMilliseconds,r.PreparationMilliseconds,r.SolarMilliseconds,r.ShadingMilliseconds,r.OutputMilliseconds });
}
var input=ShadingWorkbook.ReadIrradiance(inputPath);
var mask=SkyShadingModule.DecodeMask(File.ReadAllBytes(maskPath));
var calibration=ShadingFilePipeline.ReadCalibration(calibrationPath,mask.Width,mask.Height,66.43);
var solar=new SolarPositionModule(site);var sequence=solar.Prepare(input.Samples,sampling);
var shader=new SkyShadingModule(mask,calibration,pose,options,disk);
if(args.Contains("--convergence"))Convergence.Run(output,input,site,mask,calibration,pose,disk);
if(args.Contains("--sweep"))OptimizationSweep.Run(output,inputPath,maskPath,calibrationPath,site,pose,disk);
if(args.Contains("--annual"))AnnualBenchmark.Run(output,inputPath,maskPath,calibrationPath,site,pose,disk);
var warmTimes=new List<double>();
for(int i=0;i<repeats;i++){var sw=Stopwatch.StartNew();shader.Evaluate(sequence);warmTimes.Add(sw.Elapsed.TotalMilliseconds);}

var report=new { GeneratedUtc=DateTimeOffset.UtcNow,Site=site,Pose=pose,
    Assumptions="Elevation defaults to 0 m until supplied. Overlay is 12:00 UTC+07 (civil noon), not solar transit. Native calibration provisionally paired with matching 3000x4000 example; absolute image pose/lens pairing not field-verified.",
    Input=inputPath,Rows=input.Samples.Count,ZeroDirect=input.Samples.Count(s=>s.DirectHorizontal==0),Hardware=cold.Hardware,
    Options=options,Sampling=sampling,Cold=cold with { Result=new([],cold.Result.Diffuse,cold.Result.SolarAngularRadiusDegrees,cold.Result.DiskSamples,sampling) },
    FileToFile=timings,WarmShadingOnly=warmTimes,Diffuse=shader.Diffuse,
    StatusCounts=cold.Result.Rows.GroupBy(r=>r.Status).ToDictionary(g=>g.Key,g=>g.Count()) };
File.WriteAllText(Path.Combine(output,"benchmark.json"),JsonSerializer.Serialize(report,jsonOptions));
if(args.Contains("--nasa"))
{
    var nasaPath=inputPath.Replace("OpenMeteo_","NasaPower_");
    var nasa=ShadingFilePipeline.Compute(nasaPath,maskPath,calibrationPath,66.43,site,Path.Combine(output,"nasa-shading-month.xlsx"),null,pose,options,disk);
    File.WriteAllText(Path.Combine(output,"nasa-validation.json"),JsonSerializer.Serialize(new{Rows=nasa.Result.Rows.Count,Sampling=nasa.Result.Sampling,Statuses=nasa.Result.Rows.GroupBy(r=>r.Status).ToDictionary(g=>g.Key,g=>g.Count()),nasa.TotalMilliseconds},jsonOptions));
}
Console.WriteLine(JsonSerializer.Serialize(new{ColdMs=cold.TotalMilliseconds,FileToFile=timings,Diffuse=shader.Diffuse},jsonOptions));

// Numeric oracle inputs and results: compare directions instead of unstable azimuth close to zenith.
var oracleCases=new List<object>();
foreach(var s in new[]{site,new SolarSite(0,0,0),new SolarSite(65,25,1500),new SolarSite(-33.9,151.2,30),new SolarSite(27.98,86.92,8849)})
foreach(var date in new[]{"2025-03-20","2025-06-21","2025-12-21","2026-05-31"})
for(int h=0;h<24;h+=2)
{
    var t=DateTimeOffset.Parse(date+"T00:00:00Z",CultureInfo.InvariantCulture).AddHours(h);
    oracleCases.Add(new{Site=s,Timestamp=t,Position=new SolarPositionModule(s).Calculate(t)});
}
File.WriteAllText(Path.Combine(output,"solar-oracle-input.json"),JsonSerializer.Serialize(oracleCases,jsonOptions));

if(!args.Contains("--no-images"))
{
    // Native OpenCV overlay: no generative image manipulation; subpixel boundary comes from the actual projector.
    using var original=Cv2.ImRead(imagePath,ImreadModes.Color);
    var noon=DateTimeOffset.Parse("2026-05-31T12:00:00+07:00",CultureInfo.InvariantCulture);
    var sun=solar.Calculate(noon);
    var overlayReport=new List<object>();
    foreach(var radius in new[]{0.25,2.5})
    {
        var engine=new SkyShadingModule(mask,calibration,pose,options with {SolarAngularRadiusDegrees=radius},disk);
        var boundary=engine.ProjectBoundary(sun,256);
        if(boundary.Any(p=>p==null))throw new InvalidOperationException("Noon footprint falls outside validated coverage.");
        var points=boundary.Select(p=>new Point((int)Math.Round(p!.Value.X*256),(int)Math.Round(p.Value.Y*256))).ToArray();
        using var overlay=original.Clone();using var painted=original.Clone();
        Cv2.FillPoly(painted,[points],new Scalar(0,0,255),LineTypes.AntiAlias,8);
        Cv2.AddWeighted(painted,0.75,original,0.25,0,overlay);
        var tag=radius.ToString("0.00",CultureInfo.InvariantCulture);
        Cv2.ImWrite(Path.Combine(output,$"noon-20260531-radius-{tag}-full.jpg"),overlay);
        engine.TryProject(sun.Direction,out var px,out var py);
        var roi=new Rect(Math.Clamp((int)px-100,0,original.Width-200),Math.Clamp((int)py-100,0,original.Height-200),200,200);
        using var crop=new Mat(overlay,roi);using var enlarged=new Mat();
        Cv2.Resize(crop,enlarged,new Size(800,800),0,0,InterpolationFlags.Nearest);
        using var labeled=new Mat();Cv2.CopyMakeBorder(enlarged,labeled,105,0,0,0,BorderTypes.Constant,new Scalar(255,255,255));
        Cv2.PutText(labeled,$"Angular radius {tag} deg | actual footprint in red",new Point(12,28),HersheyFonts.HersheySimplex,0.65,Scalar.Black,1,LineTypes.AntiAlias);
        Cv2.PutText(labeled,"2026-05-31 12:00 +07 | top North, vertical up",new Point(12,57),HersheyFonts.HersheySimplex,0.65,Scalar.Black,1,LineTypes.AntiAlias);
        Cv2.PutText(labeled,$"10.8N 106.7E | elevation {Elevation}m assumed | 4x crop",new Point(12,86),HersheyFonts.HersheySimplex,0.6,Scalar.Black,1,LineTypes.AntiAlias);
        Cv2.ImWrite(Path.Combine(output,$"noon-20260531-radius-{tag}-detail.png"),labeled);
        overlayReport.Add(new{Timestamp=noon,Sun=sun,RadiusDegrees=radius,CenterX=px,CenterY=py,WidthPixels=boundary.Max(p=>p!.Value.X)-boundary.Min(p=>p!.Value.X),HeightPixels=boundary.Max(p=>p!.Value.Y)-boundary.Min(p=>p!.Value.Y)});
    }
    File.WriteAllText(Path.Combine(output,"overlay-geometry.json"),JsonSerializer.Serialize(overlayReport,jsonOptions));
}
