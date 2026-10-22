using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SolarShade.Desktop;

internal sealed class WeatherSourceSwitch : ToggleButton
{
    public WeatherSourceSwitch()
    {
        Margin = new(0, 4, 0, 8); MinHeight = 38; HorizontalContentAlignment = HorizontalAlignment.Left;
        var outline = new FrameworkElementFactory(typeof(Border)); outline.Name = "FocusOutline";
        outline.SetValue(Border.BorderBrushProperty, Brushes.Transparent); outline.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        outline.SetValue(Border.CornerRadiusProperty, new CornerRadius(6)); outline.SetValue(Border.PaddingProperty, new Thickness(3));
        var row = new FrameworkElementFactory(typeof(DockPanel)); outline.AppendChild(row);
        var track = new FrameworkElementFactory(typeof(Border)); track.Name = "Track";
        track.SetValue(DockPanel.DockProperty, Dock.Left); track.SetValue(WidthProperty, 48d); track.SetValue(HeightProperty, 26d);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(13)); track.SetValue(Border.BackgroundProperty, Brushes.SlateGray);
        var thumb = new FrameworkElementFactory(typeof(Ellipse)); thumb.Name = "Thumb";
        thumb.SetValue(WidthProperty, 18d); thumb.SetValue(HeightProperty, 18d); thumb.SetValue(MarginProperty, new Thickness(4));
        thumb.SetValue(Shape.FillProperty, Brushes.White); thumb.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        track.AppendChild(thumb); row.AppendChild(track);
        var caption = new FrameworkElementFactory(typeof(ContentPresenter));
        caption.SetValue(ContentPresenter.ContentSourceProperty, "Content"); caption.SetValue(MarginProperty, new Thickness(10, 0, 0, 0));
        caption.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); row.AppendChild(caption);
        var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = outline };
        var on = new Trigger { Property = IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right, "Thumb"));
        on.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(15, 125, 111)), "Track"));
        template.Triggers.Add(on);
        var focused = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.DarkSlateGray, "FocusOutline")); template.Triggers.Add(focused);
        Template = template;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "WeatherSourceMode");
    }
}
