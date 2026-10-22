using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class WeatherControlsTests
{
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }

    [Fact]
    public void SourceSelectionMatchesRestoredFileAndRereadsChangedImportsWithoutDownloading() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var owner = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var controls = Descendants(owner.Irradiance).ToArray();
            var source = Assert.Single(controls.OfType<ComboBox>(), c => AutomationProperties.GetName(c) == "Weather data source");
            var refresh = Assert.Single(controls.OfType<Button>(), b => Equals(b.Content, "Download weather again & update"));
            Assert.Equal(2, source.SelectedIndex); Assert.False(refresh.IsEnabled);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            double first = owner.Completed!.Run.BeforeEnergy;
            Assert.Contains(controls.OfType<TextBlock>(), b => b.Text.Contains("Last result: Imported"));
            source.SelectedIndex = 0;
            Assert.Empty(owner.IrradianceController.Settings.ImportPath);
            Assert.True(refresh.IsEnabled); Assert.Equal(1, fixture.Runs);
            owner.Irradiance.SelectWeatherFile(fixture.Settings.ImportPath);
            Assert.Equal(2, source.SelectedIndex); Assert.False(refresh.IsEnabled);
            File.WriteAllLines(fixture.Settings.ImportPath, new[] { "Timestamp,BHI,DHI" }
                .Concat(Enumerable.Range(0, 24).Select(hour => $"2025-05-15T{hour:00}:00:00+00:00,0,200")));
            await owner.RecheckSourcesAsync(); owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.Equal(2 * first, owner.Completed!.Run.BeforeEnergy, 6);
            Assert.Equal(fixture.Settings.ImportPath, owner.Completed.Settings.ImportPath);
            Assert.Contains(controls.OfType<TextBlock>(), b => b.Text.Contains("Selected file: weather.csv") && b.Text.Contains("Retrieved"));
        }
        finally { owner.Close(); }
    });
}
