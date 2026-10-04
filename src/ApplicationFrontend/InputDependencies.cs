namespace SolarShade.Desktop;

[Flags]
public enum ArtifactGroup
{
    None = 0, Profile = 1, Mask = 2, Orientation = 4, Irradiance = 8,
    Solar = 16, SunPath = 32, Transposition = 64, Shading = 128, All = 255
}

public sealed record SourceIdentity(string Path, string Content)
{
    public static SourceIdentity Read(string path, SourceIdentity? previous, bool refresh)
    {
        if (string.IsNullOrEmpty(path)) return new("", "");
        try
        {
            path = System.IO.Path.GetFullPath(PortablePaths.Resolve(path));
            if (!refresh && previous != null && string.Equals(path, previous.Path, StringComparison.OrdinalIgnoreCase)) return previous;
            return new(path, File.Exists(path) ? AppData.FileKey(path) : "missing");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(path, "unreadable"); }
    }
}

/// <summary>Dependencies of published artifacts, independent of calculation/cache implementation.</summary>
public sealed class InputDependencies
{
    public SourceIdentity Image { get; }
    public SourceIdentity Profile { get; }
    public SourceIdentity Weather { get; }
    public SourceIdentity EditedMask { get; }
    public SourceIdentity EditedMetadata { get; }
    public IReadOnlyDictionary<ArtifactGroup, string> Keys { get; }
    public IEnumerable<string> SourcePaths => new[] { Image.Path, Profile.Path, Weather.Path, EditedMask.Path, EditedMetadata.Path }.Where(p => p.Length > 0);
    public static readonly ArtifactGroup[] Groups = Enum.GetValues<ArtifactGroup>().Where(g => g is not (ArtifactGroup.None or ArtifactGroup.All)).ToArray();
    public const ArtifactGroup WeatherChain = ArtifactGroup.Irradiance | ArtifactGroup.Solar | ArtifactGroup.SunPath | ArtifactGroup.Transposition | ArtifactGroup.Shading;
    public const ArtifactGroup GeometryChain = WeatherChain & ~ArtifactGroup.Irradiance;
    public const ArtifactGroup PoseChain = ArtifactGroup.Orientation | ArtifactGroup.SunPath | ArtifactGroup.Shading;
    public const ArtifactGroup PanelChain = ArtifactGroup.Transposition | ArtifactGroup.Shading;

    private InputDependencies(UserSettings s, InputDependencies? previous, bool refresh)
    {
        Image = SourceIdentity.Read(s.SkyImage, previous?.Image, refresh);
        Profile = SourceIdentity.Read(s.ProfilePath, previous?.Profile, refresh);
        Weather = SourceIdentity.Read(s.ImportPath, previous?.Weather, refresh);
        string variantFolder = s.SelectedMaskId == null || Image.Content is "" or "missing" or "unreadable" ? ""
            : Path.GetDirectoryName(ManualMaskStore.VariantPngPath(Image.Content, s.SelectedMaskId))!;
        EditedMask = SourceIdentity.Read(variantFolder.Length == 0 ? "" : Path.Combine(variantFolder, "sky-mask.png"), previous?.EditedMask, refresh);
        EditedMetadata = SourceIdentity.Read(variantFolder.Length == 0 ? "" : Path.Combine(variantFolder, "variant.json"), previous?.EditedMetadata, refresh);
        var keys = new Dictionary<ArtifactGroup, string>();
        keys[ArtifactGroup.Mask] = AppData.Key(new { Image, s.Model, s.Resolution, s.CenteredDisk, s.SelectedMaskId, EditedMask, EditedMetadata });
        keys[ArtifactGroup.Profile] = AppData.Key(new { Profile, Image, s.CoverageAngle });
        keys[ArtifactGroup.Orientation] = AppData.Key(new { Image, s.CenteredDisk, Profile = keys[ArtifactGroup.Profile], s.BottomAzimuth, s.CameraTilt, s.CameraRoll });
        // Date/site changes do not change the contents of an imported source workbook.
        keys[ArtifactGroup.Irradiance] = s.ImportPath.Length > 0
            ? AppData.Key(new { Weather, s.ImportWindow, s.ImportIntervalMinutes, s.Zone })
            : AppData.Key(new { s.Provider, s.Start, s.End, s.Latitude, s.Longitude, s.Zone });
        keys[ArtifactGroup.Solar] = AppData.Key(new { Raw = keys[ArtifactGroup.Irradiance], s.Start, s.End, s.Zone, s.Latitude, s.Longitude, s.Elevation, s.Substeps });
        keys[ArtifactGroup.SunPath] = AppData.Key(new { Solar = keys[ArtifactGroup.Solar], Camera = keys[ArtifactGroup.Orientation], s.Zone });
        keys[ArtifactGroup.Transposition] = AppData.Key(new { Solar = keys[ArtifactGroup.Solar], s.PanelTilt, s.PanelAzimuth, s.Isotropic });
        keys[ArtifactGroup.Shading] = AppData.Key(new { Panel = keys[ArtifactGroup.Transposition], Camera = keys[ArtifactGroup.Orientation], Mask = keys[ArtifactGroup.Mask] });
        Keys = keys;
    }
    public static InputDependencies Capture(UserSettings s, InputDependencies? previous = null, bool refresh = true) => new(s, previous, refresh);
    public ArtifactGroup Difference(InputDependencies other) => Groups.Where(g => Keys[g] != other.Keys[g]).Aggregate(ArtifactGroup.None, (a, g) => a | g);

    public static ArtifactGroup WithDependents(ArtifactGroup groups)
    {
        if ((groups & ArtifactGroup.Mask) != 0) groups |= ArtifactGroup.Shading;
        if ((groups & ArtifactGroup.Profile) != 0) groups |= PoseChain;
        if ((groups & ArtifactGroup.Orientation) != 0) groups |= PoseChain;
        if ((groups & ArtifactGroup.Irradiance) != 0) groups |= WeatherChain;
        if ((groups & ArtifactGroup.Solar) != 0) groups |= GeometryChain;
        if ((groups & ArtifactGroup.Transposition) != 0) groups |= PanelChain;
        return groups;
    }

    public static ArtifactGroup ForField(string field, UserSettings settings) => field switch
    {
        "SkyImage" => ArtifactGroup.Profile | ArtifactGroup.Mask | PoseChain,
        "Model" or "Resolution" => ArtifactGroup.Mask | ArtifactGroup.Shading,
        "CenteredDisk" => ArtifactGroup.Mask | PoseChain,
        "SelectedMaskId" => ArtifactGroup.Mask | ArtifactGroup.Shading,
        "ProfilePath" or "CoverageAngle" => ArtifactGroup.Profile | PoseChain,
        "BottomAzimuth" or "CameraTilt" or "CameraRoll" => PoseChain,
        "PanelTilt" or "PanelAzimuth" or "Isotropic" => PanelChain,
        "Latitude" or "Longitude" or "Start" or "End" => settings.ImportPath.Length > 0 ? GeometryChain : WeatherChain,
        "Elevation" or "Substeps" => GeometryChain,
        "Zone" or "ImportPath" => WeatherChain,
        "ImportWindow" or "ImportIntervalMinutes" => settings.ImportPath.Length > 0 ? WeatherChain : ArtifactGroup.None,
        "Provider" => settings.ImportPath.Length == 0 ? WeatherChain : ArtifactGroup.None,
        _ => ArtifactGroup.None // View preferences and future calibration setup do not alter a loaded profile.
    };

    public static ArtifactGroup ForArtifact(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        return parts[0] switch
        {
            "01-calibration" => ArtifactGroup.Profile, "02-sky-mask" => ArtifactGroup.Mask,
            "02-orientation" => ArtifactGroup.Orientation, "03-irradiance" => ArtifactGroup.Irradiance,
            "04-solar-positions" => parts.Length > 1 && parts[1].StartsWith("sun-path-", StringComparison.Ordinal) ? ArtifactGroup.SunPath : ArtifactGroup.Solar,
            "05-transposition" => ArtifactGroup.Transposition, "06-shading" => ArtifactGroup.Shading,
            _ => ArtifactGroup.None
        };
    }
}
