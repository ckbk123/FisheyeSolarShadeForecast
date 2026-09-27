namespace SolarShade.Desktop;

public static class PortablePaths
{
    public static string Resolve(string path, string? baseDirectory = null) => string.IsNullOrWhiteSpace(path) ? "" :
        Path.GetFullPath(path, baseDirectory ?? AppContext.BaseDirectory);

    public static string Store(string path, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string root = baseDirectory ?? AppContext.BaseDirectory;
        string absolute = Resolve(path, root), relative = Path.GetRelativePath(root, absolute);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return absolute;
        return relative.Replace('\\', '/');
    }

    public static UserSettings Map(UserSettings settings, Func<string, string> map) => settings with
    {
        CalibrationFolder = map(settings.CalibrationFolder), SkyFolder = map(settings.SkyFolder),
        SkyImage = map(settings.SkyImage), ProfilePath = map(settings.ProfilePath), ImportPath = map(settings.ImportPath)
    };

    public static UserSettings Example()
    {
        var settings = AppData.Read<UserSettings>(Resolve("Example/settings.json")) ??
            throw new FileNotFoundException("The bundled example is missing. Extract the complete Deliverable folder, including Example, beside APPLICATION.exe.");
        foreach (string path in new[] { settings.SkyImage, settings.ProfilePath, settings.ImportPath })
            if (!File.Exists(Resolve(path))) throw new FileNotFoundException("Missing example file: " + path + ". Extract the complete package again.");
        if (!Directory.Exists(Resolve(settings.CalibrationFolder))) throw new DirectoryNotFoundException("Missing Example/Calib Images. Extract the complete package again.");
        return Map(settings, path => Store(path));
    }
}
