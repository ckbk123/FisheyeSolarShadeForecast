using System.IO;
using System.Text.RegularExpressions;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class OrientationWorkspaceTests
{
    [Fact]
    public void UnvisitedWorkspaceCreatesNoBrowserSurface() => Sta(() =>
    {
        using var workspace = new OrientationWorkspace();
        Assert.Null(workspace.Content);
        return Task.CompletedTask;
    });

    [Fact]
    public void EmbeddedPageAndStandaloneFallbackShareEveryLinkedScriptAndStylesheet()
    {
        var directory = Directory.CreateTempSubdirectory("solar-orientation-");
        try
        {
            var path = OrientationMapAssets.WriteStandalone(directory.FullName);
            using var index = OrientationMapAssets.Open(OrientationMapAssets.PageUrl, out var mime);
            Assert.Equal("text/html", mime);
            using var reader = new StreamReader(index!);
            string html = reader.ReadToEnd();
            Assert.Equal(html, File.ReadAllText(path));
            var links = Regex.Matches(html, "(?:src|href)=\"([^\"]+\\.(?:js|css))\"");
            Assert.NotEmpty(links);
            foreach (Match link in links)
            {
                string relative = link.Groups[1].Value;
                using var asset = OrientationMapAssets.Open(new Uri(new Uri(OrientationMapAssets.PageUrl), relative).AbsoluteUri, out mime);
                Assert.NotNull(asset);
                Assert.Equal(relative.EndsWith(".js") ? "text/javascript" : "text/css", mime);
                using var assetReader = new StreamReader(asset!);
                Assert.Equal(File.ReadAllText(Path.Combine(directory.FullName, relative)), assetReader.ReadToEnd());
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("https://different.invalid/index.html")]
    [InlineData("https://orientation.invalid:4430/index.html")]
    [InlineData("file:///C:/study/index.html")]
    [InlineData("https://orientation.invalid/settings.json")]
    public void OnlyTheBundledMapCanBeOpened(string uri)
    {
        Assert.True(OrientationMapAssets.IsPage(OrientationMapAssets.PageUrl));
        Assert.False(OrientationMapAssets.IsPage(uri));
        Assert.Null(OrientationMapAssets.Open(uri, out _));
    }
}
