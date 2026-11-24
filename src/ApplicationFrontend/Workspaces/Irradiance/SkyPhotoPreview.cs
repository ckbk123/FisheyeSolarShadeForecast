using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private Grid CreateSkyPhotoPane(RowDefinition previewRow, UIElement maskPane)
    {
        var pane = new Grid { ClipToBounds = true };
        pane.Children.Add(skyViewport);
        var tools = new StackPanel {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3)
        };
        pane.Children.Add(tools);
        Point Center() => new(skyViewport.ActualWidth / 2, skyViewport.ActualHeight / 2);
        Button Tool(string text, string name, string help, string id)
        {
            var button = new Button { Content = text, MinWidth = 23, Padding = new(5, 2, 5, 2), Margin = new(1), FontSize = 11 };
            AutomationProperties.SetName(button, name);
            AutomationProperties.SetAutomationId(button, id);
            StudioTheme.Help(button, help);
            tools.Children.Add(button);
            return button;
        }
        var minus = Tool("−", "Zoom sky photo out", "Zoom out around the center of the photo view.", "ZoomSkyPhotoOut");
        var plus = Tool("+", "Zoom sky photo in", "Zoom in around the center. Scroll on the photo to zoom toward the pointer.", "ZoomSkyPhotoIn");
        var fit = Tool("Fit", "Fit sky photo", "Center the whole photo and its overlays in the available space.", "FitSkyPhoto");
        var expand = Tool("Expand", "Expand sky photo", "Use the width of both previews to inspect the sky photo and compass.", "ExpandSkyPhoto");
        minus.Click += (_, _) => skyViewport.ZoomAt(Center(), 1 / 1.5);
        plus.Click += (_, _) => skyViewport.ZoomAt(Center(), 1.5);
        fit.Click += (_, _) => skyViewport.Fit();
        bool expanded = false;
        expand.Click += (_, _) => {
            expanded = !expanded;
            Grid.SetColumnSpan(pane, expanded ? 3 : 1);
            maskPane.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            previewRow.Height = new(expanded ? Math.Clamp(owner.ActualHeight * .55, 320, 640) : 220);
            expand.Content = expanded ? "Restore" : "Expand";
            AutomationProperties.SetName(expand, expanded ? "Restore sky photo layout" : "Expand sky photo");
            StudioTheme.Help(expand, expanded ? "Show the sky photo and mask side by side." : "Use the width of both previews to inspect the sky photo and compass.");
            if (expanded) _ = skyViewport.RequestNativePhotoAsync();
            UpdateButtons();
        };
        void UpdateButtons()
        {
            plus.IsEnabled = skyViewport.HasPhoto;
            minus.IsEnabled = fit.IsEnabled = skyViewport.Zoom > 1;
            expand.IsEnabled = skyViewport.HasPhoto || expanded;
        }
        skyViewport.ViewChanged += UpdateButtons;
        UpdateButtons();
        return pane;
    }
}
