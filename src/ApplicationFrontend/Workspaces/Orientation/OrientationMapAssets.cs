namespace SolarShade.Desktop;

/// <summary>The same offline files supply the embedded map and the standalone browser fallback.</summary>
internal static class OrientationMapAssets
{
    private const string Prefix = "OrientationMap.";
    internal const string PageUrl = "https://orientation.invalid/index.html?embedded=1";
    private static readonly System.Reflection.Assembly Assembly = typeof(OrientationMapAssets).Assembly;
    private static readonly Dictionary<string, string> Resources = Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(name => "/" + name[Prefix.Length..], StringComparer.Ordinal);

    internal static bool IsPage(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        IsLocal(uri) && uri.AbsolutePath == "/index.html";

    private static bool IsLocal(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host == "orientation.invalid" && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    internal static Stream? Open(string address, out string contentType)
    {
        contentType = "text/plain";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IsLocal(uri) ||
            !Resources.TryGetValue(uri.AbsolutePath, out var name)) return null;
        contentType = Path.GetExtension(name) switch
        {
            ".js" => "text/javascript",
            ".css" => "text/css",
            _ => "text/html"
        };
        return Assembly.GetManifestResourceStream(name);
    }

    internal static string WriteStandalone(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (path, resource) in Resources)
        {
            var destination = Path.Combine(directory, path[1..]);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temporary))
                using (var input = Assembly.GetManifestResourceStream(resource)!)
                    input.CopyTo(output);
                File.Move(temporary, destination, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        return Path.Combine(directory, "index.html");
    }
}
