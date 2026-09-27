using System.Diagnostics;
using System.Text.Json;
using SolarShade.Core.Models;
using SolarShade.Irradiance;
using SolarShade.Shading;

// dotnet run -c Release --project src/SolarPositionAndShading/OverlayBenchmark -- <output-directory> [cadence-minutes=60] [days=365] [warm-repeats=5]
string output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "overlay-benchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
int cadence = args.Length > 1 ? int.Parse(args[1]) : 60;
int days = args.Length > 2 ? int.Parse(args[2]) : 365;
int repeats = args.Length > 3 ? int.Parse(args[3]) : 5;
if (cadence <= 0 || days is < 1 or > 366 || repeats is < 1 or > 20 || 1440 % cadence != 0) throw new ArgumentException("Use a cadence dividing one day, 1-366 days and 1-20 warm repeats.");
Directory.CreateDirectory(output);
var zone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(7));
var source = IrradianceDatasets.FromSamples(Enumerable.Range(0, days * 1440 / cadence).Select(i => new IrradianceSample(start.AddMinutes(i * cadence), 0, 100)).ToArray(),
    TimeSpan.FromMinutes(cadence), TimestampLabel.Start, "Deterministic benchmark geometry; constant synthetic weather", zone.Id, "Explicit interval start");
var prepareTimer = Stopwatch.StartNew();
var timeline = new SolarPositionModule(new(10.8, 106.7, 20)).PrepareIntervals(source, 60);
double preparationMilliseconds = prepareTimer.Elapsed.TotalMilliseconds;
var calibration = new CalibrationResult(2, CameraModelKind.OmniCalibIncidentAnglePolynomial, 3000, 4000,
    [1499.5, 1999.5], [0, 2998 / Math.PI], 90, 1499, null, 0, [], DateTimeOffset.UtcNow, "Synthetic calibrated equidistant benchmark");
var measurements = new List<object>();
var elapsed = new List<double>();
var exportElapsed = new List<double>();
for (int repeat = 0; repeat <= repeats; repeat++)
{
    var allTimer = Stopwatch.StartNew();
    var projectionTimer = Stopwatch.StartNew();
    var projection = new CalibratedSkyProjection(calibration);
    double projectorMilliseconds = projectionTimer.Elapsed.TotalMilliseconds;
    var result = SunPathOverlayGenerator.Generate(timeline, projection, zone);
    double generateWithProjectorMilliseconds = allTimer.Elapsed.TotalMilliseconds;
    var exportTimer = Stopwatch.StartNew();
    var paths = SunPathOverlayGenerator.Export(result, Path.Combine(output, repeat == 0 ? "cold" : "warm"));
    double exportMilliseconds = exportTimer.Elapsed.TotalMilliseconds;
    double fullMilliseconds = allTimer.Elapsed.TotalMilliseconds;
    var record = new { Repeat = repeat, Cold = repeat == 0, ProjectorMilliseconds = projectorMilliseconds,
        result.Timings, GenerateWithProjectorMilliseconds = generateWithProjectorMilliseconds, ExportMilliseconds = exportMilliseconds,
        FullMilliseconds = fullMilliseconds, result.SelectedSampleCount, result.ProjectedSampleCount,
        result.TrackCount, Vertices = result.Vertices.Count, PngBytes = result.Png.Length,
        ArtifactBytes = paths.Sum(p => new FileInfo(p).Length) };
    measurements.Add(record);
    if (repeat > 0) { elapsed.Add(generateWithProjectorMilliseconds); exportElapsed.Add(fullMilliseconds); }
    Console.WriteLine(JsonSerializer.Serialize(record));
}
object Summary(List<double> values)
{ var sorted = values.Order().ToArray(); return new { MinimumMilliseconds = sorted[0], MedianMilliseconds = sorted[sorted.Length / 2], MaximumMilliseconds = sorted[^1] }; }
var report = new { Dimensions = "3000x4000", CadenceMinutes = cadence, Days = days, SourceIntervals = source.Intervals.Count,
    SamplesPerInterval = 60, PreparationMilliseconds = preparationMilliseconds,
    TimingScope = "First overlay call is cold including native image library initialization. Solar preparation runs separately. Every measured run includes projector creation, all selected sample projections, simplification, batch drawing and PNG encoding; full time additionally includes native PNG, compact XLSX and metadata writes. No samples or days omitted for speed.",
    WarmGenerate = Summary(elapsed), WarmGenerateAndExport = Summary(exportElapsed), Runs = measurements };
File.WriteAllText(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));
