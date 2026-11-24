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
            string standalone = File.ReadAllText(path);
            using var reader = new StreamReader(OrientationMapAssets.OpenEmbeddedPage());
            string embedded = reader.ReadToEnd();
            var references = Regex.Matches(standalone, """(?:src|href)="([^"]+\.(?:js|css))(?=")""");
            Assert.NotEmpty(references);
            int previous = 0;
            foreach (Match link in references)
            {
                string name = link.Groups[1].Value;
                string source = File.ReadAllText(Path.Combine(directory.FullName, name));
                int index = embedded.IndexOf(source, StringComparison.Ordinal);
                Assert.True(index > previous, name);
                previous = index;
                Assert.DoesNotContain(link.Value, embedded);
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
    }
}
