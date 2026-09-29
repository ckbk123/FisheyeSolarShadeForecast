using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolarShade.Irradiance;

namespace SolarShade.PvBattery.IO;

public static class BatteryFiles
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new ExplicitOffsetConverter() }
    };
    private static readonly string[] Headers = ["Interval start", "Interval end", "Partial hour", "Start stored (Wh)",
        "End stored (Wh)", "End SoC (%)", "PV (Wh)", "Load (Wh)", "Served load (Wh)", "Unmet load (Wh)", "Curtailed (Wh)"];

    public static BatterySimulationRequest ReadRequest(string path)
    {
        using var file = File.OpenRead(path);
        var request = JsonSerializer.Deserialize<BatterySimulationRequest>(file, Json) ?? throw new InvalidDataException("Null request.");
        BatterySimulator.ValidateSettings(request.Settings);
        BatterySimulator.ValidateSeries(request.Irradiance);
        return request;
    }

    public static BatterySimulationSettings ReadSettings(string path)
    {
        using var file = File.OpenRead(path);
        var settings = JsonSerializer.Deserialize<BatterySimulationSettings>(file, Json) ?? throw new InvalidDataException("Null settings.");
        BatterySimulator.ValidateSettings(settings);
        return settings;
    }

    public static void WriteJson(string path, BatterySimulationResult result, CancellationToken ct = default) =>
        Atomic(path, stream => JsonSerializer.Serialize(stream, result, Json), ct);

    public static void WriteCsv(string path, BatterySimulationResult result, CancellationToken ct = default) =>
        Atomic(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            static string Cell(object? value)
            {
                string text = value is DateTimeOffset time ? time.ToString("O", CultureInfo.InvariantCulture) :
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            writer.WriteLine(string.Join(",", Headers.Select(Cell)));
            foreach (var row in result.Hours)
            {
                ct.ThrowIfCancellationRequested();
                writer.WriteLine(string.Join(",", Values(row).Select(Cell)));
            }
        }, ct);

    public static void WriteXlsx(string path, BatterySimulationResult result, CancellationToken ct = default)
    {
        var settings = result.Input.Settings;
        var summary = result.Summary;
        object?[][] details =
        [
            ["Model version", result.ModelVersion], ["Input fingerprint (SHA-256)", result.InputFingerprint],
            ["Study time zone", result.Input.Irradiance.TimeZoneId], ["Assumptions", result.Assumptions],
            ["Panel area (m²)", settings.PanelAreaM2], ["Panel efficiency (fraction)", settings.PanelEfficiency],
            ["Conversion efficiency (fraction)", settings.ConversionEfficiency], ["Battery capacity (Wh)", settings.BatteryCapacityWh],
            ["Initial SoC (fraction)", settings.InitialSoc], ["Initial stored (Wh)", summary.InitialStoredWh],
            ["Final stored (Wh)", summary.FinalStoredWh], ["Minimum SoC (%)", summary.MinimumSocPercent],
            ["PV (Wh)", summary.PvWh], ["Load (Wh)", summary.LoadWh], ["Served load (Wh)", summary.ServedLoadWh],
            ["Unmet load (Wh)", summary.UnmetLoadWh], ["Curtailed (Wh)", summary.CurtailedWh],
            ["Hours containing shortfall", summary.HoursContainingShortfall], ["First shortfall interval start", summary.FirstShortfallIntervalStart],
            ["Unmet energy fraction", summary.UnmetEnergyFraction], ["Source fingerprint", result.Input.Irradiance.SourceFingerprint],
            ["Source description", result.Input.Irradiance.SourceDescription]
        ];
        // JSON is the full audit export, including every source interval and simulation substep.
        ScientificWorkbook.Write(path, [
            new("Hourly SoC", Headers, result.Hours.Select(Values)),
            new("Settings and summary", ["Quantity", "Value"], details),
            new("Daily load", ["Local hour", "Load (Wh per nominal hour)"], settings.HourlyLoadWh.Select((load, hour) => new object?[] { hour, load }))
        ], $"PV-battery model {result.ModelVersion}. Input SHA-256={result.InputFingerprint}. {result.Assumptions}", ct);
    }

    private static object?[] Values(BatteryHour h) => [h.Start, h.End, h.IsPartialHour, h.StartStoredWh, h.EndStoredWh,
        h.EndSocPercent, h.PvWh, h.LoadWh, h.ServedLoadWh, h.UnmetLoadWh, h.CurtailedWh];

    private static void Atomic(string path, Action<Stream> write, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary)) write(stream);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class ExplicitOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String ||
                !DateTimeOffset.TryParseExact(reader.GetString(), ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
                throw new JsonException("Timestamps require an explicit UTC offset.");
            return value;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
    }
}
