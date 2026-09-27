using System.Globalization;
using System.Text.Json;
using SolarShade.Irradiance;
using SolarShade.Irradiance.Validation;

// Defaults are May 2025 in Ho Chi Minh City; all dates are ISO, end exclusive.
if (args.Contains("--help"))
{
    Console.WriteLine("[--start yyyy-MM-dd] [--end yyyy-MM-dd] [--lat number] [--lon number] [--service All|NasaPower|OpenMeteo|Nsrdb|Cams|Oikolab|CopernicusCds] [--output directory]");
    return 0;
}
string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
DateOnly Date(string name, string fallback) => DateOnly.ParseExact(Arg(name) ?? fallback, "yyyy-MM-dd", CultureInfo.InvariantCulture);
var start = Date("--start", "2025-05-01"); var end = Date("--end", "2025-06-01");
double lat = double.Parse(Arg("--lat") ?? "10.8", CultureInfo.InvariantCulture);
double lon = double.Parse(Arg("--lon") ?? "106.7", CultureInfo.InvariantCulture);
var output = Path.GetFullPath(Arg("--output") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(output);
var options = new IrradianceOptions
{
    NsrdbApiKey = Environment.GetEnvironmentVariable("NSRDB_API_KEY"),
    NsrdbEmail = Environment.GetEnvironmentVariable("NSRDB_EMAIL"),
    CamsEmail = Environment.GetEnvironmentVariable("CAMS_EMAIL"),
    OikolabApiKey = Environment.GetEnvironmentVariable("OIKOLAB_API_KEY"),
    CdsPersonalAccessToken = Environment.GetEnvironmentVariable("CDS_PERSONAL_ACCESS_TOKEN")
};
var client = new IrradianceClient(options);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var services = Arg("--service") is string selection && selection != "All"
    ? new[] { Enum.Parse<IrradianceService>(selection, true) } : Enum.GetValues<IrradianceService>();
var summaries = new List<object>(); bool failed = false;
async Task Run(IrradianceService service, DateOnly a, DateOnly b, string label)
{
    var request = new IrradianceRequest(a, b, lon, lat, service);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var result = await client.PullDetailedAsync(request, Path.Combine(output, $"{service}_{a:yyyyMMdd}_{b:yyyyMMdd}_{label}.xlsx"), cancellation.Token);
    bool verified = false;
    if (result.Succeeded) { WorkbookValidator.Validate(result.OutputPath!, result.Samples, options.TimeZone); verified = true; }
    failed |= !result.Succeeded;
    Console.WriteLine($"{service} {a:yyyy-MM-dd}..{b:yyyy-MM-dd} status={result.Status}, rows={result.Samples.Count}, {watch.Elapsed.TotalSeconds:F2}s: {result.Message}");
    summaries.Add(new { Service = service.ToString(), Start = a, EndExclusive = b, Latitude = lat, Longitude = lon,
        result.Status, result.Message, Rows = result.Samples.Count, result.OutputPath, result.TimeZoneId,
        result.TimestampConvention, WorkbookVerified = verified, Seconds = watch.Elapsed.TotalSeconds, Label = label });
}
foreach (var service in services) await Run(service, start, end, "requested");
var summary = new { GeneratedUtc = DateTimeOffset.UtcNow, Hardware = HardwareCapabilities.Detect(), Runs = summaries };
await File.WriteAllTextAsync(Path.Combine(output, "validation-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Results: " + output);
// Conventional process exit codes: 0 all passed, 1 any failed/unconfigured. Library status uses opposite convention.
return failed ? 1 : 0;
