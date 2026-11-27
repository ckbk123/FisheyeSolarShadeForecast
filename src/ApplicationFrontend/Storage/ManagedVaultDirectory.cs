namespace SolarShade.Desktop;

/// <summary>Publish an archive atomically beside files that a user kept in its former directory.</summary>
internal static class ManagedVaultDirectory
{
    private const string Prefix = ".solarshade-result-";
    private sealed record Marker(string? Owner, string? Key, string? SourceKey, string? SettingsKey);

    public static IEnumerable<string> Locations(string baseFolder)
    {
        yield return baseFolder;
        foreach (string child in PhotoStorageCleanup.Directories(baseFolder))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..], "N", out _))
                yield return child;
        }
    }

    public static string? Find(string baseFolder, string owner, string key, string? sourceKey = null) =>
        Locations(baseFolder).FirstOrDefault(folder =>
        {
            var marker = PhotoStorageCleanup.Read<Marker>(Path.Combine(folder, "vault.json"));
            return marker?.Owner == owner && (sourceKey == null ? marker.Key == key :
                marker.SourceKey == sourceKey && marker.SettingsKey == key);
        });

    // Whole-directory rename remains atomic, including when the standard directory contains personal files.
    public static string NewDestination(string baseFolder) => Directory.Exists(baseFolder)
        ? Path.Combine(baseFolder, Prefix + Guid.NewGuid().ToString("N")) : baseFolder;
}
