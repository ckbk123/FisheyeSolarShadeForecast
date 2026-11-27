using System.Text.Json;

namespace SolarShade.Desktop;

internal sealed record EquipmentMapAngles(double PanelTilt, double PanelTopHeading, double PhoneTilt, double PhoneTopHeading)
{
    internal static EquipmentMapAngles? Read(string message)
    {
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 5 ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !type.ValueEquals("applyEquipmentAngles")) return null;
            return Angle("panelTilt", 90) is double pt && Angle("panelTopHeading", 360) is double ph &&
                Angle("phoneTilt", 90) is double ft && Angle("phoneTopHeading", 360) is double fh
                ? new(pt, ph, ft, fh) : null;

            double? Angle(string name, double max) => root.TryGetProperty(name, out var item) &&
                item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var value) &&
                value >= 0 && value <= max ? value : null;
        }
        catch (JsonException) { return null; }
    }
}
