using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class NonblockingHelpTests
{
    [Theory]
    [InlineData("irradiance")]
    [InlineData("pv")]
    public void HelpLeavesStudyEnabledReusesWindowAndClosesWithOwner(string workspace) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        HelpWindow? help = null;
        try
        {
            window.Show();
            window.Workspaces.SetAvailability(workspace, true, null); Assert.True(window.Workspaces.Select(workspace));
            var actions = (Panel)(workspace == "pv" ? window.PvAutonomy.Actions : window.Irradiance.Actions);
            var button = actions.Children.OfType<Button>().Single(b => Equals(b.Content, "Help"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            help = window.OwnedWindows.OfType<HelpWindow>().Single();
            Assert.True(IsWindowEnabled(new WindowInteropHelper(window).Handle));
            window.EditFieldForTest("PanelTilt", "41");
            Assert.Equal(41, window.IrradianceController.Settings.PanelTilt);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(help, window.OwnedWindows.OfType<HelpWindow>().Single());
            help.Close();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reopened = window.OwnedWindows.OfType<HelpWindow>().Single();
            Assert.NotSame(help, reopened); help = reopened;
            Assert.True(IsWindowEnabled(new WindowInteropHelper(window).Handle));
        }
        finally { window.Close(); await window.InputsSettled; }
        Assert.False(help?.IsVisible);
    });

    [Fact]
    public void ThirdPartyNoticesAlsoLeaveTheStudyEnabled() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            window.Show();
            ((Panel)window.Irradiance.Actions).Children.OfType<Button>().Single(b => Equals(b.Content, "Help"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var help = window.OwnedWindows.OfType<HelpWindow>().Single();
            var panel = (Panel)((ScrollViewer)help.Content).Content;
            var noticesButton = panel.Children.OfType<Button>().Single(b => Equals(b.Content, "Third-party notices"));
            noticesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var notices = help.OwnedWindows.OfType<HelpWindow>().Single();
            noticesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(notices, help.OwnedWindows.OfType<HelpWindow>().Single());
            Assert.True(IsWindowEnabled(new WindowInteropHelper(window).Handle));
            Assert.True(IsWindowEnabled(new WindowInteropHelper(help).Handle));
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr handle);
}
