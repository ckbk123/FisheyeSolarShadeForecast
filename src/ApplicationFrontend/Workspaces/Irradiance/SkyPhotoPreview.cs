using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private Grid CreatePreviewPair(RowDefinition previewRow)
    {
        var pair = new Grid { Margin = new(12, 0, 12, 0) };
        pair.ColumnDefinitions.Add(new());
        pair.ColumnDefinitions.Add(new() { Width = new GridLength(12) });
        pair.ColumnDefinitions.Add(new());

        var skyPane = new Grid { ClipToBounds = true };
        var maskPane = new Grid { ClipToBounds = true };
        Grid.SetColumn(maskPane, 2);
        pair.Children.Add(skyPane);
        pair.Children.Add(maskPane);
        skyPane.Children.Add(skyViewport);
        maskPane.Children.Add(maskViewport);
        maskPane.Children.Add(editMask);

        AddPreviewTools(skyViewport, skyPane, maskPane, previewRow, "SkyPhoto", "sky photo");
        AddPreviewTools(maskViewport, maskPane, skyPane, previewRow, "SkyMask", "mask");
        return pair;
    }

    private void AddPreviewTools(ImageViewport viewport, Grid pane, Grid otherPane, RowDefinition previewRow, string id, string name)
    {
        int column = Grid.GetColumn(pane);
        var tools = new StackPanel {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3)
        };
        pane.Children.Add(tools);
        Point Center() => new(viewport.ActualWidth / 2, viewport.ActualHeight / 2);
        Button Tool(string text, string action, string help, string controlId)
        {
            var button = new Button { Content = text, MinWidth = 23, Padding = new(5, 2, 5, 2), Margin = new(1), FontSize = 11 };
            AutomationProperties.SetName(button, action);
            AutomationProperties.SetAutomationId(button, controlId);
            StudioTheme.Help(button, help);
            tools.Children.Add(button);
            return button;
        }
        var minus = Tool("−", $"Zoom {name} out", "Zoom out around the center.", $"Zoom{id}Out");
        var plus = Tool("+", $"Zoom {name} in", "Zoom in around the center. Scroll to zoom toward the pointer.", $"Zoom{id}In");
        var fit = Tool("Fit", $"Fit {name}", $"Center the whole {name} and its overlays.", $"Fit{id}");
        var expand = Tool("Expand", $"Expand {name}", $"Use the width of both previews to inspect the {name}.", $"Expand{id}");
        minus.Click += (_, _) => viewport.ZoomAt(Center(), 1 / 1.5);
        plus.Click += (_, _) => viewport.ZoomAt(Center(), 1.5);
        fit.Click += (_, _) => viewport.Fit();
        bool expanded = false;
        expand.Click += (_, _) => {
            expanded = !expanded;
            Grid.SetColumn(pane, expanded ? 0 : column);
            Grid.SetColumnSpan(pane, expanded ? 3 : 1);
            otherPane.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            previewRow.Height = new(expanded ? Math.Clamp(owner.ActualHeight * .55, 320, 640) : 220);
            expand.Content = expanded ? "Restore" : "Expand";
            AutomationProperties.SetName(expand, expanded ? $"Restore {name} layout" : $"Expand {name}");
            StudioTheme.Help(expand, expanded ? "Show the sky photo and mask side by side." : $"Use the width of both previews to inspect the {name}.");
            if (expanded) viewport.RequestDetail();
            UpdateButtons();
        };
        void UpdateButtons()
        {
            plus.IsEnabled = viewport.HasImage;
            minus.IsEnabled = fit.IsEnabled = viewport.Zoom > 1;
            expand.IsEnabled = viewport.HasImage || expanded;
        }
        viewport.ViewChanged += UpdateButtons;
        UpdateButtons();
    }
}
