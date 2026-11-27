using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SolarShade.Desktop;
using SolarShade.Irradiance;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class WeatherControlsTests
{
    [Fact]
    public void TemplateImportsItsPartialDayAndRestoresWithHiddenServiceDates() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var template = Path.Combine(fixture.Root, "weather-template.xlsx");
        WeatherSourceSelection.WriteImportTemplate(template);
        var serviceStart = new DateTime(2020, 1, 1);
        AppData.SaveSettings(fixture.Settings with { Start = serviceStart, End = serviceStart, ImportWindow = 0 });
        var owner = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            owner.Irradiance.SelectWeatherFile(template);
            await owner.InputsSettled;
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.True(owner.ExportEnabledForTest, AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))?.Error ?? owner.IrradianceController.Source.Reason);
            var source = Assert.IsType<IrradianceSnapshot>(owner.IrradianceController.Source.Snapshot);
            var start = new DateTimeOffset(2025, 5, 15, 8, 0, 0, TimeSpan.FromHours(7));
            Assert.Equal(start, source.Start); Assert.Equal(start.AddHours(3), source.End);
            Assert.Equal(3, source.Run.Rows.Count);
            Assert.Equal(serviceStart, owner.IrradianceController.Settings.Start);
            var graphDay = Descendants(owner.Irradiance).OfType<DatePicker>().Single();
            Assert.Equal(start.Date, graphDay.DisplayDateStart); Assert.Equal(start.Date, graphDay.DisplayDateEnd);
            var manifest = AppData.Read<DebugDataRun.Manifest>(Path.Combine(fixture.Debug, "run.json"))!;
            Assert.Equal(new StudyPeriod(start, start.AddHours(3)), manifest.StudyPeriod);
            Assert.True(owner.Workspaces.Select("pv"));
            var pv = owner.PvAutonomy.Controller;
            pv.SetDraft(PvSettingsDraft.Example());
            await pv.EvaluateAsync(); Assert.True(pv.IsCurrent, pv.Status);
            var hours = pv.Result!.Hours;
            Assert.Equal(start, hours[0].Start); Assert.Equal(source.End, hours[^1].End);
            owner.Irradiance.SelectWeatherMode(true); await owner.InputsSettled;
            var provider = Descendants(owner.Irradiance).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Weather data source");
            provider.SelectedIndex = 1 - provider.SelectedIndex;
            owner.EditFieldForTest("Start", "bad date"); await owner.InputsSettled;
            owner.Irradiance.SelectWeatherMode(false); await owner.InputsSettled;
            Assert.True(owner.ExportEnabledForTest);
            Assert.Equal(start, owner.IrradianceController.Source.Snapshot!.Start);
        }
        finally { owner.Close(); await owner.InputsSettled; }
    });

    [Fact]
    public void NativeDatasetTimestampIsConvertedBeforeLabellingItUtc() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var data = IrradianceDatasetFiles.Import(fixture.Settings.ImportPath, TimeZoneInfo.Utc,
            TimeSpan.FromHours(1), TimestampLabel.Start) with
        { FetchedUtc = new DateTimeOffset(2025, 5, 15, 12, 0, 0, TimeSpan.FromHours(7)) };
        string workbook = Path.Combine(fixture.Root, "offset-timestamp.xlsx");
        IrradianceDatasetFiles.Export(data, workbook);
        var owner = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            owner.Irradiance.SelectWeatherFile(workbook); owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.NotNull(owner.Completed);
            var details = Descendants(owner.Irradiance).OfType<Expander>().Single(e => Equals(e.Header, "Study details"));
            Assert.False(details.IsExpanded);
            Assert.Equal(Visibility.Visible, details.Visibility);
            details.IsExpanded = true;
            Assert.Contains(Descendants(details).OfType<TextBlock>(),
                text => text.Text.Contains("Dataset timestamp: 2025-05-15 05:00 UTC."));
        }
        finally { owner.Close(); }
    });

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
            var refresh = Assert.Single(controls.OfType<Button>(), b => AutomationProperties.GetAutomationId(b) == "RefreshData");
            var mode = Assert.Single(controls.OfType<ComboBox>(), c => AutomationProperties.GetAutomationId(c) == "WeatherSourceMode");
            Assert.Equal(1, mode.SelectedIndex); Assert.Equal("My Excel or CSV file", mode.SelectedItem); Assert.False(refresh.IsEnabled);
            owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            double first = owner.Completed!.Run.BeforeEnergy;
            Assert.Contains(controls.OfType<TextBlock>(), b => b.Text.Contains("Last result: Imported"));
            owner.SetProfileForTest(fixture.Settings.ProfilePath);
            Assert.Contains(controls.OfType<TextBlock>(), b => b.Text.Contains("Last result: Imported"));
            mode.SelectedIndex = 0; source.SelectedIndex = 0;
            Assert.Empty(owner.IrradianceController.Settings.ImportPath);
            Assert.True(refresh.IsEnabled); Assert.Equal(1, fixture.Runs);
            owner.Irradiance.SelectWeatherFile(fixture.Settings.ImportPath);
            Assert.Equal(1, mode.SelectedIndex); Assert.False(refresh.IsEnabled);
            File.WriteAllLines(fixture.Settings.ImportPath, new[] { "Timestamp,BHI,DHI" }
                .Concat(Enumerable.Range(0, 24).Select(hour => $"2025-05-15T{hour:00}:00:00+00:00,0,200")));
            await owner.RecheckSourcesAsync(); owner.Calculate(); await Until(() => !owner.UpdatingForTest);
            Assert.Equal(2 * first, owner.Completed!.Run.BeforeEnergy, 6);
            Assert.Equal(fixture.Settings.ImportPath, owner.Completed.Settings.ImportPath);
            Assert.Contains(controls.OfType<TextBlock>(), b => b.Text.Contains("Selected file: weather.csv"));
            Assert.Contains(controls.OfType<TextBlock>(), b => b.Text.Contains("Last result: Imported") && b.Text.Contains("Dataset timestamp:"));
        }
        finally { owner.Close(); }
    });
}
