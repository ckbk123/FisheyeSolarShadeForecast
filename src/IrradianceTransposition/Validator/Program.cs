using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Transposition;
using SolarShade.Irradiance.Transposition.Validation;
using SolarShade.Shading;

// No download or credential needed: the validator consumes the previously validated raw provider exports.
// Usage: dotnet run --project <this project> -c Release -- [input-folder] [output-folder] [tilt] [azimuth] [substeps]
var root = FindRoot();
var source = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "src/IrradianceDataExtractor/SolarShade.Irradiance.Validation/results/live-20260905");
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, "src/IrradianceTransposition/Validator/results/may2025");
var panel = new PanelOrientation(args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 30,
    args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0);
int substeps = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 60;
Directory.CreateDirectory(output);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
var reference = ReferenceChecks.Run(Path.Combine(AppContext.BaseDirectory, "pvlib-0.15.2.json"));
Console.WriteLine(JsonSerializer.Serialize(reference, jsonOptions));
var sanity = new List<object>();
foreach (var model in new[] { DiffuseModel.HayDavies, DiffuseModel.PerezDriesse })
{
    var horizontal = SkyTransposition.EvaluateDni(new(0, 180), new(60, 180), 800, 100, 1366.1, model);
    var towardSun = SkyTransposition.EvaluateDni(new(60, 180), new(60, 180), 800, 100, 1366.1, model);
    var away = SkyTransposition.EvaluateDni(new(60, 0), new(60, 180), 800, 100, 1366.1, model);
    if (Math.Abs(horizontal.Direct - 400) > 1e-9 || Math.Abs(towardSun.Direct - 800) > 1e-9 || away.Direct > 1e-9 || towardSun.Total <= horizontal.Total)
        throw new InvalidDataException("Tilt-toward-Sun sanity check failed.");
    sanity.Add(new { Model = model, Zenith = 60, SunAzimuth = 180, Dni = 800, Dhi = 100, Horizontal = horizontal, TowardSun = towardSun, Away = away });
}
var summaries = new List<object>();
bool failed = false;
foreach (var service in new[] { IrradianceService.NasaPower, IrradianceService.OpenMeteo })
{
    string inputPath = Path.Combine(source, $"{service}_20250501_20250601_requested.xlsx");
    var input = ShadingWorkbook.ReadIrradiance(inputPath);
    if (input.Latitude is null || input.Longitude is null || input.SuggestedSampling is null)
        throw new InvalidDataException("Input must contain site and averaging-window metadata.");
    var solar = new SolarPositionModule(new(input.Latitude.Value, input.Longitude.Value, 0));
    SunPosition Position(DateTimeOffset t)
    {
        var s = solar.Calculate(t);
        return SunPosition.FromGeometricNoaa(s.ZenithDegrees, s.AzimuthDegrees);
    }
    var window = new SamplingWindow(input.SuggestedSampling.StartOffsetMinutes, input.SuggestedSampling.DurationMinutes, substeps);
    var options = new TranspositionOptions
    {
        MaxDegreeOfParallelism = 0, // The NOAA/Meeus callback is stateless and safe for parallel calls.
        Provenance = $"NOAA/Meeus geometric solar zenith converted to apparent zenith using the explicit NOAA standard-atmosphere refraction approximation; elevation 0 m. Original input: {Path.GetFileName(inputPath)}. {input.Metadata}"
    };
    foreach (var model in new[] { DiffuseModel.HayDavies, DiffuseModel.PerezDriesse })
    {
        string name = FormattableString.Invariant($"{service}_{model}_tilt{panel.TiltDegrees}_az{panel.AzimuthDegrees}");
        string workbookPath = Path.Combine(output, name + ".xlsx");
        var stopwatch = Stopwatch.StartNew();
        var result = TranspositionModule.ComputeToWorkbook(input.Samples, panel, Position, window, workbookPath, model, options);
        stopwatch.Stop();
        Console.WriteLine($"{name}: status {result.Status}; {result.Message} ({stopwatch.Elapsed.TotalMilliseconds:F1} ms including export)");
        if (!result.Succeeded) { failed = true; summaries.Add(new { Service = service, Model = model, result.Status, result.Message }); continue; }
        // Doubling quadrature resolution is a convergence diagnostic, not a claim about sub-hour cloud accuracy.
        var finer = TranspositionModule.Compute(input.Samples, panel, Position, window with { Samples = substeps * 2 }, model, options);
        if (!finer.Succeeded) throw new InvalidDataException(finer.Message);
        double maxResolutionDifference = result.Samples.Zip(finer.Samples, (a, b) => Math.Max(Math.Abs(a.Direct - b.Direct), Math.Abs(a.SkyDiffuse - b.SkyDiffuse))).Max();
        summaries.Add(new
        {
            Service = service, Model = model, result.Status, result.Message, Workbook = Path.GetFileName(workbookPath), Rows = result.Samples.Count,
            ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            DirectKwhM2 = result.Samples.Sum(s => s.Direct) / 1000,
            SkyDiffuseKwhM2 = result.Samples.Sum(s => s.SkyDiffuse) / 1000,
            MaximumInferredDni = result.Samples.Max(s => s.EffectiveDni),
            MaximumDifferenceWithDoubleSubstepsWm2 = maxResolutionDifference
        });
        var audit = new
        {
            Model = model, Tilt = panel.TiltDegrees, Azimuth = panel.AzimuthDegrees, Window = window,
            Workbook = Path.GetFileName(workbookPath), Input = inputPath,
            Rows = result.Samples.Select((row, i) =>
            {
                var raw = input.Samples[i];
                var times = raw.DirectHorizontal == 0 && raw.DiffuseHorizontal == 0 ? Array.Empty<DateTimeOffset>()
                    : Enumerable.Range(0, substeps).Select(j => raw.TimestampUtc.AddMinutes(window.StartOffsetMinutes + (j + .5) * window.DurationMinutes / substeps)).ToArray();
                return new
                {
                    row.Timestamp, row.Direct, row.SkyDiffuse, row.DirectFactor, row.DiffuseFactor,
                    row.EffectiveDni, row.MeanDaylightCosine, row.Flags,
                    ExportUtcOffsetSeconds = options.TimeZone.GetUtcOffset(row.Timestamp).TotalSeconds,
                    Bhi = raw.DirectHorizontal, Dhi = raw.DiffuseHorizontal, Times = times, Positions = times.Select(Position).ToArray()
                };
            }).ToArray()
        };
        using var auditFile = File.Create(Path.Combine(output, name + "_audit.json.gz"));
        using var gzip = new GZipStream(auditFile, CompressionLevel.SmallestSize);
        JsonSerializer.Serialize(gzip, audit, jsonOptions);
    }
}
File.WriteAllText(Path.Combine(output, "validation.json"), JsonSerializer.Serialize(new
{
    Reference = reference, Sanity = sanity, Runs = summaries, Hardware = HardwareCapabilities.Detect(),
    Notes = "Unshaded front-side irradiance, no ground reflection. This validates implementation, not field accuracy. Input data: May 2025 HCM City, default panel 30 degrees toward true north. Hourly sums / 1000 give kWh/m²."
}, jsonOptions));
return failed ? 1 : 0;

static string FindRoot()
{
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var p = new DirectoryInfo(start); p is not null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, "SolarShade.sln"))) return p.FullName;
    throw new DirectoryNotFoundException("Run within the SolarShade workspace.");
}
