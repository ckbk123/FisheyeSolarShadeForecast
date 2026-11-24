using SolarShade.Irradiance;

namespace SolarShade.Desktop;

internal static class WeatherSourceSelection
{
    public static bool UsesServices(UserSettings settings) => settings.UseDataServices ?? string.IsNullOrWhiteSpace(settings.ImportPath);
    public static string ActiveImportPath(UserSettings settings) => UsesServices(settings) ? "" : settings.ImportPath;

    public static void SetMode(UserSettings settings, bool useServices)
    {
        if (!string.IsNullOrWhiteSpace(settings.ImportPath)) settings.RememberedImportPath = settings.ImportPath;
        settings.UseDataServices = useServices;
        settings.ImportPath = useServices ? "" : settings.RememberedImportPath;
    }

    public static void SelectFile(UserSettings settings, string path)
    {
        settings.UseDataServices = false;
        settings.ImportPath = settings.RememberedImportPath = PortablePaths.Store(path);
    }

    internal static void WriteImportTemplate(string path) =>
        ScientificWorkbook.Write(path, [
            new("Weather", ["Timestamp", "Direct horizontal (W/m²)", "Diffuse horizontal (W/m²)"], [
                ["2025-05-15T09:00:00+07:00", 0, 65],
                ["2025-05-15T10:00:00+07:00", 0, 80],
                ["2025-05-15T11:00:00+07:00", 0, 92]
            ]),
            new ScientificSheet("Instructions", ["How to fill in Weather"], [
                ["Replace the three example rows with readings for your site. Keep the headers and chronological order."],
                ["The sample represents three overcast hours. Each timestamp marks the end of a 60-minute interval."],
                ["Use your source’s interval length and timestamp convention in SolarShade. Include a UTC offset for repeated local times."],
                ["Enter mean direct and diffuse irradiance on a horizontal surface in W/m². Enter numbers in both columns for each interval."]
            ]) { ColumnWidths = [115] }
        ], "Editable weather import template with hourly, end-labelled example data.");

    public static UserSettings Normalize(UserSettings settings)
    {
        var result = settings with { };
        if (UsesServices(result) && !string.IsNullOrWhiteSpace(result.ImportPath))
        { result.RememberedImportPath = result.ImportPath; result.ImportPath = ""; }
        return result;
    }

    public static string? Problem(UserSettings settings)
    {
        if (UsesServices(settings)) return settings.Provider is 0 or 1 ? null : "Choose NASA POWER or Open-Meteo.";
        return string.IsNullOrWhiteSpace(settings.ImportPath) ? "Choose an Excel or CSV file for Import Own Excel." : null;
    }

    public static void RequireValid(UserSettings settings)
    {
        if (Problem(settings) is { } problem) throw new InvalidOperationException(problem);
    }
}
