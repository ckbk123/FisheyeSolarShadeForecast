using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SolarShade.Desktop;
using SolarShade.Irradiance;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class WeatherSourceModeTests
{
    [Fact]
    public void LegacySettingsInferModeAndSwitchingRemembersFileAndProvider()
    {
        var settings = JsonSerializer.Deserialize<UserSettings>("{\"ImportPath\":\"weather.xlsx\",\"Provider\":1}")!;
        Assert.False(WeatherSourceSelection.UsesServices(settings));
        WeatherSourceSelection.SetMode(settings, true);
        Assert.Empty(settings.ImportPath); Assert.Equal("weather.xlsx", settings.RememberedImportPath); Assert.Equal(1, settings.Provider);
        settings = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        Assert.True(WeatherSourceSelection.UsesServices(settings));
        WeatherSourceSelection.SetMode(settings, false);
        Assert.Equal("weather.xlsx", settings.ImportPath); Assert.Equal(1, settings.Provider);
        Assert.True(WeatherSourceSelection.UsesServices(JsonSerializer.Deserialize<UserSettings>("{}")!));
    }

    [Fact]
    public void EmptyImportModeHasDifferentDependenciesAndVaultIdentityFromServices()
    {
        var services = new UserSettings { UseDataServices = true };
        var import = services with { UseDataServices = false };
        Assert.Equal(InputDependencies.WeatherChain, InputDependencies.Capture(services).Difference(InputDependencies.Capture(import)));
        Assert.NotEqual(AcceptedResultVault.Key(services), AcceptedResultVault.Key(import));
        Assert.Contains("Choose an Excel", UpdateInputs.Problem(import));
        Assert.Equal(InputDependencies.WeatherChain, InputDependencies.ForField("UseDataServices", import));
        Assert.Equal(ArtifactGroup.None, InputDependencies.Capture(services).Difference(InputDependencies.Capture(services with
        { ImportPath = "inactive-missing.xlsx", RememberedImportPath = "older.xlsx", ImportIntervalMinutes = -1 })));
        Assert.Empty(InputDependencies.Capture(services with { ImportPath = "inactive-missing.xlsx" }).Weather.Path);
    }

    [Fact]
    public void RememberedFilesRemainPortableAndMissingInactiveImportsDoNotBlockPreparation()
    {
        var saved = PortablePaths.Map(new UserSettings { UseDataServices = true, RememberedImportPath = @"C:\Package\Data\weather.xlsx" },
            path => PortablePaths.Store(path, @"C:\Package"));
        Assert.Equal("Data/weather.xlsx", saved.RememberedImportPath);
        Assert.Equal(@"D:\Moved\Data\weather.xlsx", PortablePaths.Map(saved, path => PortablePaths.Resolve(path, @"D:\Moved")).RememberedImportPath);
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var settings = fixture.Settings with { UseDataServices = true, ImportPath = "", RememberedImportPath = Path.Combine(fixture.Debug, "gone.xlsx") };
        Assert.Equal(settings.RememberedImportPath, service.PrepareInputs(settings).RememberedImportPath);
    }

    [Fact]
    public async Task EmptyImportCannotFetchOrEvaluateOrRestoreAServiceResult()
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var settings = fixture.Settings with { UseDataServices = false, ImportPath = "" };
        Assert.Null(service.TryRestoreAccepted(settings));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppServices.Fetch(settings, false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Evaluate(settings, false, _ => { }, null, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(AppData.Root, "weather-stages"))); Assert.Equal(0, fixture.Runs);
    }

    [Fact]
    public void ToggleShowsOnlyTheChosenControlsAndAnEmptyImportStaysPending() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        AppData.SaveSettings(fixture.Settings with { UseDataServices = true, ImportPath = "", Provider = 1 });
        var owner = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var controls = Descendants(owner.Irradiance).ToArray();
            var mode = Assert.Single(controls.OfType<ToggleButton>(), c => AutomationProperties.GetAutomationId(c) == "WeatherSourceMode");
            var provider = Assert.Single(controls.OfType<ComboBox>(), c => AutomationProperties.GetName(c) == "Weather data source");
            var choose = Assert.Single(controls.OfType<Button>(), c => Equals(c.Content, "Choose XLSX / CSV file"));
            var update = Assert.Single(controls.OfType<Button>(), c => AutomationProperties.GetAutomationId(c) == "UpdateResults");
            var refresh = Assert.Single(controls.OfType<Button>(), c => AutomationProperties.GetAutomationId(c) == "RefreshData");
            Assert.True(mode.IsChecked); Assert.Equal("Use Data Services", mode.Content); Assert.Equal(2, provider.Items.Count);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)provider.Parent).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)choose.Parent).Visibility);
            mode.IsChecked = false; await owner.InputsSettled;
            Assert.Equal("Import Own Excel", mode.Content); Assert.False(update.IsEnabled); Assert.False(refresh.IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)provider.Parent).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)choose.Parent).Visibility);
            owner.Calculate(); await owner.InputsSettled;
            Assert.Null(owner.Completed); Assert.Equal(0, fixture.Runs); Assert.Empty(owner.IrradianceController.Settings.ImportPath);
            Assert.False(AppData.ReadSettings()!.UseDataServices);
            // Cancelling the file picker makes no selection and therefore leaves this pending mode intact.
            await owner.RecheckSourcesAsync(); Assert.False(mode.IsChecked); Assert.False(update.IsEnabled);
            owner.Irradiance.SelectWeatherFile(fixture.Settings.ImportPath); await owner.InputsSettled;
            Assert.True(update.IsEnabled);
            mode.IsChecked = true; await owner.InputsSettled;
            Assert.Equal(1, provider.SelectedIndex); Assert.Empty(owner.IrradianceController.Settings.ImportPath);
            mode.IsChecked = false; await owner.InputsSettled;
            Assert.Equal(fixture.Settings.ImportPath, owner.IrradianceController.Settings.ImportPath); Assert.True(update.IsEnabled);
        }
        finally { owner.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }
}
