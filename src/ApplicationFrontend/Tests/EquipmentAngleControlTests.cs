using System.Windows;
using System.Windows.Controls;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class EquipmentAngleControlTests
{
    [Fact]
    public void EditingUsesSouthZeroAndFlatHeadingAndCopiesPanelOnlyOnRequest() => Sta(async () =>
    {
        using var fixture = new Fixture();
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var controls = Descendants(window.Irradiance).ToList();
            var settings = window.IrradianceController.Settings;
            TextBox Box(string field) => controls.OfType<TextBox>().Single(box => Equals(box.Tag, field));
            void Edit(string field, string value) => window.CommitFieldForTest(field, value);
            var copy = controls.OfType<Button>().Single(b => Equals(b.Content, "Copy panel pose to phone"));

            Assert.False(Box("PhoneFacingAzimuth").IsEnabled);
            Assert.Equal("—", Box("PhoneFacingAzimuth").Text);
            Assert.True(Box("FlatImageTopBearing").IsEnabled);
            Edit("FlatImageTopBearing", "90");
            Assert.Equal(270, settings.BottomAzimuth, 9);
            Edit("PanelTilt", "28");
            var panelSlider = controls.OfType<Slider>().Single(s => Equals(s.Tag, "PanelFacingAzimuth"));
            panelSlider.Value = -180;
            Assert.Equal(-180, panelSlider.Value);
            Assert.Equal(0, settings.PanelAzimuth);
            Edit("PanelFacingAzimuth", "270");
            Assert.Equal("-90", Box("PanelFacingAzimuth").Text);
            Assert.Equal(90, settings.PanelAzimuth);
            Assert.Equal(0, settings.CameraTilt);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("28", Box("PhoneInclination").Text);
            Assert.Equal("-90", Box("PhoneFacingAzimuth").Text);
            Assert.False(Box("FlatImageTopBearing").IsEnabled);
            Assert.Equal(90, settings.BottomAzimuth); Assert.Equal(-28, settings.CameraTilt);
            Edit("PhoneFacingAzimuth", "90");
            Assert.Equal(270, settings.BottomAzimuth);
            Assert.Equal(90, settings.PanelAzimuth);
            Edit("PhoneInclination", "-28");
            Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest);
            Assert.Equal(-28, settings.CameraTilt);
            Edit("PhoneInclination", "0");
            Assert.Equal("—", Box("PhoneFacingAzimuth").Text);
            Assert.Equal(90, settings.FlatImageTopBearing, 9);
            Edit("PanelTilt", "0");
            Assert.Equal("—", Box("PanelFacingAzimuth").Text);
            Assert.False(Box("PanelFacingAzimuth").IsEnabled);
            Assert.False(panelSlider.IsEnabled);
            Assert.NotEqual(UpdateState.NeedsAttention, window.UpdateStateForTest);
            Assert.Equal(0, fixture.Runs);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void ExistingDownhillPoseSurvivesLoadAndCalculationUntilExplicitUphillAction() => Sta(async () =>
    {
        using var fixture = new Fixture();
        AppData.SaveSettings(fixture.Settings with { BottomAzimuth = 180, CameraTilt = 28, CameraRoll = 10 });
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var controls = Descendants(window.Irradiance).ToList();
            var tilt = controls.OfType<TextBox>().Single(b => Equals(b.Tag, "PhoneInclination"));
            var reset = controls.OfType<Button>().Single(b => Equals(b.Content, "Use lens-end-uphill placement"));
            Assert.False(tilt.IsEnabled); Assert.Equal(Visibility.Visible, reset.Visibility);
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.NotNull(window.Completed);
            var original = window.Completed!.Settings;
            Assert.Equal(28, original.CameraTilt); Assert.Equal(10, original.CameraRoll); Assert.Equal(180, original.BottomAzimuth);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = window.IrradianceController.Settings;
            Assert.True(tilt.IsEnabled); Assert.Equal(Visibility.Collapsed, reset.Visibility);
            Assert.Equal(-28, settings.CameraTilt); Assert.Equal(0, settings.CameraRoll);
            Assert.Equal(0, settings.BottomAzimuth); Assert.Equal(180, settings.PhoneFacingAzimuth);
            Assert.False(window.ExportEnabledForTest);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void DownwardLensSeparatesZenithFromInclinationAndTypingADecimalPreservesTheRolledFrame() => Sta(async () =>
    {
        using var fixture = new Fixture();
        AppData.SaveSettings(fixture.Settings with { BottomAzimuth = 90, CameraTilt = 180 });
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var controls = Descendants(window.Irradiance).ToList();
            var tilt = controls.OfType<TextBox>().Single(b => Equals(b.Tag, "PhoneInclination"));
            Assert.Equal("—", tilt.Text); Assert.False(tilt.IsEnabled);
            Assert.Contains(controls.OfType<TextBlock>(), b => b.Text.Contains("Lens zenith 180°") && b.Text.Contains("face -90° from south"));
            var reset = controls.OfType<Button>().Single(b => Equals(b.Content, "Use lens-end-uphill placement"));
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("90", tilt.Text); Assert.Equal(-90, window.IrradianceController.Settings.PhoneFacingAzimuth);

            window.SetCameraPoseForTest(145.3, -28, 15);
            window.EditFieldForTest("PhoneInclination", "0");
            window.CommitFieldForTest("PhoneInclination", "0.8");
            var changed = window.IrradianceController.Settings;
            Assert.Equal(-0.8, changed.CameraTilt);
            Assert.Equal(15, changed.CameraRoll);
            Assert.Equal(145.3, changed.BottomAzimuth);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }
}
