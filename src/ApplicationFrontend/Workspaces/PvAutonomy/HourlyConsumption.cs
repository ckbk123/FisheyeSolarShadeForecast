using System.Globalization;

namespace SolarShade.Desktop;

internal enum HourlyConsumptionUnit { Wh, J, W, VaDc }

internal static class HourlyConsumption
{
    public static double ToWh(string text, HourlyConsumptionUnit unit)
    {
        double entered = PvSettingsDraft.Number(text, "Consumption");
        if (entered < 0) throw new ArgumentException("Consumption cannot be negative.");
        double wh = unit switch
        {
            HourlyConsumptionUnit.Wh or HourlyConsumptionUnit.W or HourlyConsumptionUnit.VaDc => entered,
            HourlyConsumptionUnit.J => entered / 3600,
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        // Validate the complete replacement before any of the 24 slots is changed.
        if (!double.IsFinite(wh * 24)) throw new ArgumentException("Consumption is too large for a daily total.");
        if (entered > 0 && wh == 0) throw new ArgumentException("Consumption is too small to represent in Wh.");
        return wh;
    }

    public static string Convert(string text, HourlyConsumptionUnit from, HourlyConsumptionUnit to)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        double wh = ToWh(text, from);
        double converted = to switch
        {
            HourlyConsumptionUnit.Wh or HourlyConsumptionUnit.W or HourlyConsumptionUnit.VaDc => wh,
            HourlyConsumptionUnit.J => wh * 3600,
            _ => throw new ArgumentOutOfRangeException(nameof(to))
        };
        if (!double.IsFinite(converted)) throw new ArgumentException("Consumption is too large for the selected unit.");
        return Format(converted);
    }

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
