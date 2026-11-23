using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var topics = ((Panel)((ScrollViewer)help.Content).Content).Children.OfType<Expander>().ToArray();
            Assert.True(topics.Length > 1);
            Assert.Same(topics[0], Assert.Single(topics, topic => topic.IsExpanded));
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<TextBlock>(topics[0].Content).Text));
            Assert.All(topics.Skip(1), topic => Assert.Null(topic.Content));

            var second = topics[1];
            var header = Assert.IsType<ToggleButton>(second.Template.FindName("HeaderSite", second));
            Assert.True(header.Focusable && header.IsTabStop);
            FocusManager.SetFocusedElement(help, header);
            Assert.Same(header, FocusManager.GetFocusedElement(help));
            var peer = UIElementAutomationPeer.CreatePeerForElement(second)!;
            var topicAction = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse));
            topicAction.Expand();
            Assert.True(second.IsExpanded);
            var body = Assert.IsType<TextBlock>(second.Content);
            Assert.False(string.IsNullOrWhiteSpace(body.Text));
            Assert.All(topics.Skip(2), topic => Assert.Null(topic.Content));
            topicAction.Collapse(); Assert.False(second.IsExpanded);
            topicAction.Expand(); Assert.True(second.IsExpanded);
            Assert.Same(body, second.Content);
            Assert.True(IsWindowEnabled(new WindowInteropHelper(window).Handle));
            window.EditFieldForTest("PanelTilt", "41");
            Assert.Equal(41, window.IrradianceController.Settings.PanelTilt);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(help, window.OwnedWindows.OfType<HelpWindow>().Single());
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(header)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            header.RaiseEvent(escape);
            Assert.True(escape.Handled); Assert.False(help.IsVisible);
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
            var noticesPanel = Assert.IsType<StackPanel>(((ScrollViewer)notices.Content).Content);
            Assert.Empty(noticesPanel.Children.OfType<Expander>());
            Assert.Contains(noticesPanel.Children.OfType<TextBlock>(), text => text.Text.Contains("BSD 3-Clause License"));
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
