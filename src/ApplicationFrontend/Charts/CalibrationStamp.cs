using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace SolarShade.Desktop;

/// <summary>Screen-space warning shared by the sky photo and checkerboard review.</summary>
internal sealed class CalibrationStamp : Grid
{
    internal const string FailureLabel = "CALIBRATION FAILED";
    internal const string UnusableLabel = "PROFILE UNUSABLE";

    private readonly TextBlock label = new()
    {
        FontFamily = StudioTheme.Font, FontWeight = FontWeights.Bold, FontSize = 23,
        TextAlignment = TextAlignment.Center, LineHeight = 25, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
    };
    private readonly Border badge;

    internal CalibrationStamp()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
        Brush foreground = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.White;
        label.Foreground = foreground;
        badge = new Border
        {
            BorderBrush = foreground, BorderThickness = new(2), CornerRadius = new(4), Padding = new(3),
            Child = new Border {
                BorderBrush = foreground, BorderThickness = new(1), Padding = new(13, 4, 13, 6),
                Child = label
            },
            LayoutTransform = new RotateTransform(-7)
        };
        // Keep the warning legible in a narrow portrait preview and proportionate in the expanded view.
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new(8, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = new(28, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = new(44, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = new(28, GridUnitType.Star) });
        var fit = new Viewbox {
            Child = badge, MaxWidth = 410, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        SetColumn(fit, 1);
        SetRow(fit, 1);
        Children.Add(fit);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    internal void SetMessage(string? message, string? detail = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(this, "");
            AutomationProperties.SetHelpText(this, "");
            return;
        }
        int split = message.LastIndexOf(' ');
        label.Text = split > 0 ? message[..split] + "\n" + message[(split + 1)..] : message;
        badge.Background = SystemParameters.HighContrast ? SystemColors.HighlightBrush
            : message == UnusableLabel ? Brushes.SaddleBrown : Brushes.DarkRed;
        bool changed = Visibility != Visibility.Visible || AutomationProperties.GetName(this) != message
            || AutomationProperties.GetHelpText(this) != (detail ?? "");
        AutomationProperties.SetName(this, message);
        AutomationProperties.SetHelpText(this, detail ?? "");
        Visibility = Visibility.Visible;
        if (changed && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
