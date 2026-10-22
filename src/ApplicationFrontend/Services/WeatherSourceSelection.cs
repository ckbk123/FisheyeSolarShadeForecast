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
