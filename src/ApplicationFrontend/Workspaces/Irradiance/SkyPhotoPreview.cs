using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed partial class IrradianceWorkspace
{
    private Grid CreatePreviewPair()
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
        StudioTheme.Help(noMaskStamp, NoMaskHelp);
        AutomationProperties.SetAutomationId(noMaskStamp, "NoMaskStamp");
        AutomationProperties.SetName(noMaskStamp, "No mask");
        AutomationProperties.SetLiveSetting(noMaskStamp, AutomationLiveSetting.Polite);
        maskPane.Children.Add(noMaskStamp);
        maskPane.Children.Add(editMask);

        AddPreviewTools(skyViewport, skyPane, maskPane, "SkyPhoto", "sky photo");
        AddPreviewTools(maskViewport, maskPane, skyPane, "SkyMask", "mask");
        return pair;
    }

    private void AddPreviewTools(ImageViewport viewport, Grid pane, Grid otherPane, string id, string name)
    {
        int column = Grid.GetColumn(pane);
        var tools = new ImageZoomTools(viewport, id, name);
        pane.Children.Add(tools);
        var expand = tools.AddButton("Expand", $"Expand {name}", $"Use the width of both previews to inspect the {name}.", $"Expand{id}");
        bool expanded = false;
        expand.Click += (_, _) => {
            expanded = !expanded;
            Grid.SetColumn(pane, expanded ? 0 : column);
            Grid.SetColumnSpan(pane, expanded ? 3 : 1);
            otherPane.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            previewExpanded = expanded; FitPreviewHeight();
            expand.Content = expanded ? "Restore" : "Expand";
            AutomationProperties.SetName(expand, expanded ? $"Restore {name} layout" : $"Expand {name}");
            StudioTheme.Help(expand, expanded ? "Show the sky photo and mask side by side." : $"Use the width of both previews to inspect the {name}.");
            if (expanded) viewport.RequestDetail();
            UpdateExpand();
        };
        void UpdateExpand() => expand.IsEnabled = viewport.HasImage || expanded;
        viewport.ViewChanged += UpdateExpand;
        UpdateExpand();
    }
}
