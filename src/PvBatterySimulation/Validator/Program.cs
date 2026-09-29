using SolarShade.PvBattery;
using SolarShade.PvBattery.IO;
using SolarShade.PvBattery.Validator;

if (args.Contains("--help"))
{
    Console.WriteLine("""
        Offline PV-battery validator
          No input options: run the bundled hand-calculated two-day fixture.
          --request <request.json> [--expected <expected.json>]
          --panel-xlsx <panel-shaded.xlsx> --settings <settings.json> --time-zone <id>
          Either mode accepts --output <directory> (default: artifacts/pv-battery).
        Writes battery-hourly.csv, battery-result.json and battery-hourly.xlsx.
        Exit 0: all requested checks passed. Exit 1: invalid input, failed check or export. Exit 130: cancelled.
        """);
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    string[] allowed = ["--request", "--expected", "--panel-xlsx", "--settings", "--time-zone", "--output"];
    var options = new Dictionary<string, string>();
    for (int i = 0; i < args.Length; i += 2)
        if (!allowed.Contains(args[i]) || i + 1 == args.Length || args[i + 1].StartsWith("--") || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Unknown, duplicate or incomplete option. Use --help.");
    BatterySimulationRequest request;
    string? expected = options.GetValueOrDefault("--expected");
    if (options.TryGetValue("--panel-xlsx", out string? workbook))
    {
        if (options.ContainsKey("--request") || !options.ContainsKey("--settings") || !options.ContainsKey("--time-zone"))
            throw new ArgumentException("Workbook mode requires --settings and --time-zone and cannot also specify --request.");
        request = new(BatteryFiles.ReadSettings(options["--settings"]), PanelWorkbookReader.Read(workbook, options["--time-zone"], cancellation.Token));
    }
    else
    {
        if (options.ContainsKey("--settings") || options.ContainsKey("--time-zone")) throw new ArgumentException("--settings and --time-zone require --panel-xlsx.");
        if (!options.TryGetValue("--request", out var path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-day-request.json");
            expected ??= Path.Combine(AppContext.BaseDirectory, "Fixtures", "two-day-expected.json");
        }
        request = BatteryFiles.ReadRequest(path);
    }
    var result = BatterySimulator.Compute(request, cancellation.Token);
    ReferenceChecks.Verify(result);
    if (expected is not null) ReferenceChecks.VerifyFixture(result, expected);
    string output = options.GetValueOrDefault("--output", Path.Combine("artifacts", "pv-battery"));
    BatteryFiles.WriteCsv(Path.Combine(output, "battery-hourly.csv"), result, cancellation.Token);
    BatteryFiles.WriteJson(Path.Combine(output, "battery-result.json"), result, cancellation.Token);
    BatteryFiles.WriteXlsx(Path.Combine(output, "battery-hourly.xlsx"), result, cancellation.Token);
    Console.WriteLine($"PASS: {result.Hours.Count} hourly rows, {result.Steps.Count} simulation steps. Decimal balance and aggregation checked.");
    if (expected is not null) Console.WriteLine("PASS: every frozen hourly SoC and all frozen summary values matched.");
    Console.WriteLine(FormattableString.Invariant($"Final SoC {result.Hours[^1].EndSocPercent:F3}%; unmet load {result.Summary.UnmetLoadWh:F6} Wh; curtailed {result.Summary.CurtailedWh:F6} Wh."));
    Console.WriteLine($"Exports: {Path.GetFullPath(output)}");
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (Exception e) when (e is ArgumentException or IOException or System.Text.Json.JsonException or
    ArithmeticException or TimeZoneNotFoundException or InvalidTimeZoneException or System.Xml.XmlException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"FAILED: {e.Message}");
    return 1;
}
