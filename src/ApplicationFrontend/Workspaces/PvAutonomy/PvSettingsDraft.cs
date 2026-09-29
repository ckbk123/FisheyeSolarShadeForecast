using System.Globalization;
using SolarShade.PvBattery;

namespace SolarShade.Desktop;

public sealed record PvSettingsDraft
{
    public int Version { get; init; } = 1;
    public string Area { get; init; } = "";
    public string PanelEfficiency { get; init; } = "";
    public string ConversionEfficiency { get; init; } = "";
    public string Capacity { get; init; } = "";
    public string InitialSoc { get; init; } = "";
    public string[] Load { get; init; } = Enumerable.Repeat("", 24).ToArray();
    public PvSettingsDraft Copy() => this with { Load = Load.ToArray() };
    public static PvSettingsDraft Example() => new() { Area = "2", PanelEfficiency = "20", ConversionEfficiency = "90",
        Capacity = "1000", InitialSoc = "50", Load = Enumerable.Range(0, 24).Select(h => h >= 18 && h < 23 ? "60" : "20").ToArray() };
    public static double Number(string? text, string label)
    {
        if (!double.TryParse(text?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new ArgumentException(label + ": enter a finite number.");
        return value;
    }
    public BatterySimulationSettings Parse()
    {
        if (Version != 1 || Load == null || Load.Length != 24) throw new ArgumentException("Exactly 24 consumption slots are required.");
        var load = Load.Select((v, h) => Number(v, $"Consumption at {h:00}:00")).ToArray();
        for (int h = 0; h < load.Length; h++) if (load[h] < 0) throw new ArgumentException($"Consumption at {h:00}:00 cannot be negative.");
        double area = Number(Area, "Panel area"), panel = Number(PanelEfficiency, "Panel efficiency"),
            conversion = Number(ConversionEfficiency, "Conversion efficiency"), capacity = Number(Capacity, "Battery capacity"), soc = Number(InitialSoc, "Initial charge");
        if (area < 0) throw new ArgumentException("Panel area cannot be negative.");
        if (panel <= 0 || panel > 100) throw new ArgumentException("Panel efficiency must be above 0 and at most 100%.");
        if (conversion <= 0 || conversion > 100) throw new ArgumentException("Conversion efficiency must be above 0 and at most 100%.");
        if (capacity <= 0) throw new ArgumentException("Battery capacity must be above 0 Wh.");
        if (soc < 0 || soc > 100) throw new ArgumentException("Initial charge must be between 0 and 100%.");
        var settings = new BatterySimulationSettings(area, panel / 100, conversion / 100, load, capacity, soc / 100);
        BatterySimulator.ValidateSettings(settings); return settings;
    }
    public static string[] ParsePaste(string text)
    {
        // Spreadsheet tabs/newlines delimit values; comma is a decimal separator, never an ambiguous CSV delimiter.
        var values = text.Replace("\r\n", "\n").TrimEnd('\r', '\n').Split(new[] { '\t', '\r', '\n', ';' }, StringSplitOptions.None);
        if (values.Length != 24) throw new ArgumentException("Paste exactly 24 values separated by tabs or newlines.");
        foreach (string value in values) if (Number(value, "Consumption") < 0) throw new ArgumentException("Consumption cannot be negative.");
        return values.Select(v => v.Trim()).ToArray();
    }
    public static PvSettingsDraft Restore(string path)
    {
        var draft = AppData.Read<PvSettingsDraft>(path);
        return draft is { Version: 1, Load.Length: 24 } ? draft.Copy() : new();
    }
}
