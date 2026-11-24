using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class EquipmentAngleControlTests
{
    [Fact]
    public void FlatTiltedAndVerticalPosesKeepTheSameTopHeadingControls() => Sta(async () =>
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
            var panelSlider = controls.OfType<Slider>().Single(s => Equals(s.Tag, "PanelTopHeading"));
            var phoneTop = Box("PhoneTopHeading");
            var panelTop = Box("PanelTopHeading");

            Assert.True(phoneTop.IsEnabled);
            var phoneTilt = Box("PhoneInclination");
            Assert.Equal("Phone tilt", AutomationProperties.GetName(phoneTilt));
            Assert.Equal("Phone top heading", AutomationProperties.GetName(phoneTop));
            Assert.Equal("Panel tilt", AutomationProperties.GetName(Box("PanelTilt")));
            Assert.Equal("Panel top heading", AutomationProperties.GetName(panelTop));
            foreach (var field in new[] { phoneTilt, phoneTop, Box("PanelTilt"), panelTop })
            {
                var help = Assert.IsType<string>(field.ToolTip);
                Assert.Contains("°", help);
                Assert.Contains("Orientation map", help);
                Assert.Equal(help, AutomationProperties.GetHelpText(field));
            }
            Assert.DoesNotContain(controls.OfType<TextBlock>(), b => b.Text.StartsWith("Lens azimuth"));
            Assert.DoesNotContain(controls.OfType<TextBlock>(), b => b.Text.StartsWith("Panel front azimuth") || b.Text.StartsWith("N 0°"));
            Assert.Contains("Lens azimuth undefined · flat", AutomationProperties.GetHelpText(phoneTop));
            Edit("PhoneTopHeading", "90");
            Assert.Equal(270, settings.BottomAzimuth, 9);
            Edit("PanelTilt", "28");
            panelSlider.Value = 360;
            Assert.Equal(360, panelSlider.Value);
            Assert.Equal(180, settings.PanelAzimuth);
            Edit("PanelTopHeading", "-90");
            Assert.Equal("270", panelTop.Text);
            Assert.Equal(90, settings.PanelAzimuth);
            Assert.Contains("Panel front azimuth -90°", AutomationProperties.GetHelpText(panelTop));
            Assert.Equal(AutomationProperties.GetHelpText(panelTop), AutomationProperties.GetHelpText(panelSlider));
            Assert.Equal(0, settings.CameraTilt);

            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("28", Box("PhoneInclination").Text);
            Assert.Equal("270", phoneTop.Text);
            Assert.Equal(90, settings.BottomAzimuth);
            Assert.Equal(-28, settings.CameraTilt);
            Assert.Contains("Lens azimuth -90°", AutomationProperties.GetHelpText(phoneTop));
            Edit("PhoneTopHeading", "90");
            Assert.Equal(270, settings.BottomAzimuth);
            Assert.Equal(90, settings.PanelAzimuth);
            Edit("PhoneInclination", "-28");
            Assert.Equal(UpdateState.NeedsAttention, window.UpdateStateForTest);
            Assert.Equal(-28, settings.CameraTilt);
            Edit("PhoneInclination", "0");
            Assert.True(phoneTop.IsEnabled);
            Assert.Equal("90", phoneTop.Text);

            Edit("PanelTilt", "0");
            Assert.True(panelTop.IsEnabled);
            Assert.True(panelSlider.IsEnabled);
            Assert.Contains("Panel front azimuth undefined · flat", AutomationProperties.GetHelpText(panelTop));
            Edit("PanelTopHeading", "invalid");
            Assert.False(copy.IsEnabled);
            Edit("PanelTopHeading", "180");
            Assert.True(copy.IsEnabled);
            Assert.Equal("90", phoneTop.Text);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("180", phoneTop.Text);
            Assert.Equal(0, settings.PhoneInclination);
            Assert.NotEqual(UpdateState.NeedsAttention, window.UpdateStateForTest);

            Edit("PanelTilt", "90");
            Assert.Equal("180", panelTop.Text);
            Edit("PanelTopHeading", "45");
            Assert.Equal("180", phoneTop.Text);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("45", phoneTop.Text);
            Assert.Equal(90, settings.PhoneInclination);
            Assert.Contains("Lens azimuth 45°", AutomationProperties.GetHelpText(phoneTop));
            Edit("PhoneInclination", "0");
            Assert.Equal("45", phoneTop.Text);
            Assert.Equal(0, fixture.Runs);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void InvalidStoredPanelBearingCanBeRepairedWhileFlat() => Sta(async () =>
    {
        using var fixture = new Fixture();
        AppData.SaveSettings(fixture.Settings with { PanelTilt = 0, PanelAzimuth = -1 });
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var top = Descendants(window.Irradiance).OfType<TextBox>().Single(b => Equals(b.Tag, "PanelTopHeading"));
            Assert.True(top.IsEnabled);
            Assert.Equal("—", top.Text);
            window.CommitFieldForTest("PanelTopHeading", "179");
            Assert.Equal(359, window.IrradianceController.Settings.PanelAzimuth);
            Assert.Equal("179", top.Text);
            Assert.NotEqual(UpdateState.NeedsAttention, window.UpdateStateForTest);
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
            var reset = controls.OfType<Button>().Single(b => Equals(b.Content, "Use lens-end-uphill placement"));
            Assert.Contains("Lens zenith 180°", AutomationProperties.GetHelpText(reset));
            Assert.Contains("face -90° from south", AutomationProperties.GetHelpText(reset));
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

    [Fact]
    public void UphillPhotoWithSidewaysRollStaysRolledUntilAlignedExplicitly() => Sta(async () =>
    {
        using var fixture = new Fixture();
        AppData.SaveSettings(fixture.Settings with { BottomAzimuth = 180, CameraTilt = -28, CameraRoll = 45 });
        using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var controls = Descendants(window.Irradiance).ToList();
            var heading = controls.OfType<TextBox>().Single(b => Equals(b.Tag, "PhoneTopHeading"));
            var notice = controls.OfType<TextBlock>().Single(b => b.Text == "Saved camera roll 45°");
            var align = controls.OfType<Button>().Single(b => Equals(b.Content, "Use two-angle pose"));
            Assert.Equal(Visibility.Visible, notice.Visibility);
            Assert.Equal(Visibility.Visible, align.Visibility);
            Assert.True(heading.IsEnabled);
            Assert.Equal("315", heading.Text);
            window.Calculate(); await Until(() => !window.UpdatingForTest);
            var original = window.Completed!.Settings;
            Assert.Equal(45, original.CameraRoll);
            var axis = EquipmentOrientation.ToCameraPose(original).Axes().Z;

            align.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = window.IrradianceController.Settings;
            Assert.Equal("0", heading.Text);
            Assert.Equal(-28, settings.CameraTilt);
            Assert.Equal(0, settings.CameraRoll);
            Assert.Equal(1, axis.Dot(EquipmentOrientation.ToCameraPose(settings).Axes().Z), 12);
            Assert.Equal(Visibility.Collapsed, notice.Visibility);
            Assert.Equal(Visibility.Collapsed, align.Visibility);
            Assert.False(window.ExportEnabledForTest);
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
