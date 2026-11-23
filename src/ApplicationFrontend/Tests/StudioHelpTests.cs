using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class StudioHelpTests
{
    [Fact]
    public void StyledSelectorOpensItsChoiceListAndReflectsSelection() => Sta(async () =>
    {
        var selector = new ComboBox { ItemsSource = new[] { "UTC", "Local" }, SelectedIndex = 0 };
        var window = new Window { Content = selector, Width = 260, Height = 100, ShowActivated = false, ShowInTaskbar = false };
        StudioTheme.Apply(window);
        WindowWorkArea.FitOnStartup(window);
        try
        {
            window.Show();
            selector.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = Assert.IsType<Popup>(selector.Template.FindName("PART_Popup", selector));
            Assert.True(popup.IsOpen);
            Assert.True(((ComboBoxItem)selector.ItemContainerGenerator.ContainerFromIndex(1)).IsVisible);
            selector.SelectedIndex = 1;
            selector.IsDropDownOpen = false;
            Assert.Equal("Local", selector.SelectionBoxItem);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void FieldHelpIsSharedWithAutomationAndKeyboardHelpDoesNotCommitInput() => Sta(async () =>
    {
        var box = new TextBox { Text = "28" };
        var window = new Window { Content = box, Width = 320, Height = 160, ShowActivated = false, ShowInTaskbar = false };
        const string explanation = "Measure phone tilt from horizontal, with the lens end uphill.";
        StudioTheme.Apply(window);
        StudioTheme.Help(box, explanation);
        WindowWorkArea.FitOnStartup(window);
        try
        {
            window.Show();
            Assert.Equal(explanation, box.ToolTip);
            Assert.Equal(explanation, AutomationProperties.GetHelpText(box));
            Assert.True(ToolTipService.GetShowOnDisabled(box));
            Assert.True(Press(Key.F1).Handled);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var locals = window.GetLocalValueEnumerator();
            ToolTip? tip = null;
            while (locals.MoveNext())
                if (locals.Current.Value is ToolTip popup) tip = popup;
            Assert.NotNull(tip);
            Assert.True(tip!.IsOpen);
            Assert.Equal(explanation, tip.Content);
            Assert.Same(box, tip.PlacementTarget);
            Assert.True(Press(Key.Escape).Handled);
            Assert.False(tip.IsOpen);
            Assert.Equal("28", box.Text);
        }
        finally { window.Close(); }

        KeyEventArgs Press(Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            box.RaiseEvent(args);
            return args;
        }
    });
}
