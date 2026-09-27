using System.Diagnostics;
using System.Globalization;
using SolarShade.Core.Models;
using SolarShade.Camera;

namespace SolarShade.Shading;

public sealed record FileRun(ShadingResult Result, double TotalMilliseconds, double InputMilliseconds,
    double PreparationMilliseconds, double SolarMilliseconds, double ShadingMilliseconds, double OutputMilliseconds,
    ShadingHardware Hardware);

/// <summary>Optional composition layer; neither computational module requires Excel or file paths.</summary>
public static class ShadingFilePipeline
{
    public static FileRun Compute(string irradianceXlsx, string maskPng, string calibrationYaml,
        double validatedMaximumIncidentAngleDegrees, SolarSite site, string outputXlsx,
        TimeSampling? sampling = null, CameraPose? pose = null, ShadingOptions? options = null,
        ImageDisk? imageDisk = null, CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew(); var stage = Stopwatch.StartNew();
        var hardware = ShadingHardwareDetector.Detect();
        var input = ShadingWorkbook.ReadIrradiance(irradianceXlsx);
        if ((input.Latitude is double lat && Math.Abs(lat-site.LatitudeDegrees)>1e-8) ||
            (input.Longitude is double lon && Math.Abs(lon-site.LongitudeDegrees)>1e-8))
            throw new ArgumentException("Solar site differs from the location recorded by the irradiance extractor.");
        sampling ??= input.SuggestedSampling ?? throw new ArgumentException("Provider interval convention is unspecified. Supply Instant, PrecedingHour, FollowingHour, or CenteredHour explicitly.");
        if (input.Dataset != null && input.SuggestedSampling is { } sourceSampling &&
            (sampling.StartOffsetMinutes != sourceSampling.StartOffsetMinutes || sampling.DurationMinutes != sourceSampling.DurationMinutes))
            throw new ArgumentException("The supplied sampling window disagrees with the workbook's authoritative source intervals.");
        var mask = SkyShadingModule.DecodeMask(File.ReadAllBytes(maskPng));
        var calibration = ReadCalibration(calibrationYaml, mask.Width, mask.Height, validatedMaximumIncidentAngleDegrees);
        var inputMs = stage.Elapsed.TotalMilliseconds; stage.Restart();
        pose ??= new(); options ??= new();
        var engine = new SkyShadingModule(mask,calibration,pose,options,imageDisk);
        var prepMs = stage.Elapsed.TotalMilliseconds; stage.Restart();
        var solar=new SolarPositionModule(site);
        var allPositions=solar.Calculate(input.Samples.Select(s=>s.TimestampUtc).ToArray());
        var sequence = solar.Prepare(input.Samples,sampling,options.MaxDegreeOfParallelism,cancellationToken);
        var solarMs = stage.Elapsed.TotalMilliseconds; stage.Restart();
        var result = engine.Evaluate(sequence,cancellationToken);
        var shadeMs = stage.Elapsed.TotalMilliseconds; stage.Restart();
        ShadingWorkbook.WriteSolarPositions(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputXlsx))!,Path.GetFileNameWithoutExtension(outputXlsx)+"-solar-positions.xlsx"),allPositions,site,cancellationToken);
        ShadingWorkbook.Write(outputXlsx,result,site,pose,
            $"{input.Metadata} Calibration: {Path.GetFileName(calibrationYaml)}; caller-validated max incident angle {validatedMaximumIncidentAngleDegrees.ToString(CultureInfo.InvariantCulture)} degrees. Mask: {Path.GetFileName(maskPng)}.",cancellationToken);
        return new(result,total.Elapsed.TotalMilliseconds,inputMs,prepMs,solarMs,shadeMs,stage.Elapsed.TotalMilliseconds,hardware);
    }

    /// <summary>Consumes the native solver YAML and legacy YAML. Coverage must be supplied from independent calibration validation,
    /// not inferred from a polynomial maximum. Dimensions are checked against image_size where supplied.</summary>
    public static CalibrationResult ReadCalibration(string path,int orientedWidth,int orientedHeight,double validatedMaximumIncidentAngleDegrees)
        => CameraProfileFiles.Load(path, orientedWidth, orientedHeight, validatedMaximumIncidentAngleDegrees).Calibration;
}
