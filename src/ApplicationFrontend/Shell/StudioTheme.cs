using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SolarShade.Desktop;

/// <summary>Shared native desktop resources and on-demand help for study controls.</summary>
public static class StudioTheme
{
    public static FontFamily Font { get; } = new("Inter, Roboto, Segoe UI, Arial");
    public static SolidColorBrush Ink { get; } = ColorBrush(0x1C, 0x2D, 0x40);
    public static SolidColorBrush Muted { get; } = ColorBrush(0x52, 0x65, 0x79);
    public static SolidColorBrush Navy { get; } = ColorBrush(0x17, 0x27, 0x38);
    public static SolidColorBrush Surface => Brushes.White;
    public static SolidColorBrush Canvas { get; } = ColorBrush(0xF3, 0xF6, 0xF8);
    public static SolidColorBrush Line { get; } = ColorBrush(0xD0, 0xDB, 0xE4);
    public static SolidColorBrush Blue { get; } = ColorBrush(0x34, 0x66, 0x9A);
    public static SolidColorBrush Teal { get; } = ColorBrush(0x08, 0x79, 0x73);
    public static SolidColorBrush Amber { get; } = ColorBrush(0x9A, 0x64, 0x0F);
    public static SolidColorBrush Gold { get; } = ColorBrush(0xFF, 0xD0, 0x83);
    private static readonly DependencyProperty ActiveTipProperty =
        DependencyProperty.RegisterAttached("ActiveTip", typeof(ToolTip), typeof(StudioTheme));

    private static SolidColorBrush ColorBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // Each UI dispatcher owns its WPF resources; related windows share the same compiled styles.
    [ThreadStatic] private static ResourceDictionary? styles;
    internal static void Apply(Window window)
    {
        window.FontFamily = Font;
        if (!SystemParameters.HighContrast)
        {
            styles ??= new ResourceDictionary { Source = new Uri("/APPLICATION;component/Shell/StudioStyles.xaml", UriKind.Relative) };
            if (window.Resources.MergedDictionaries.Contains(styles)) return;
            window.Resources.MergedDictionaries.Add(styles);
            window.Foreground = Ink;
        }
        window.PreviewKeyDown += OnHelpKey;
    }

    internal static void Inherit(Window dialog, Window owner)
    {
        if (owner is MainWindow || (styles != null && owner.Resources.MergedDictionaries.Contains(styles)))
            Apply(dialog);
    }

    public static void Help(FrameworkElement element, string text)
    {
        if (Equals(element.ToolTip, text) && AutomationProperties.GetHelpText(element) == text) return;
        element.ToolTip = text;
        AutomationProperties.SetHelpText(element, text);
        ToolTipService.SetShowOnDisabled(element, true);
        ToolTipService.SetShowsToolTipOnKeyboardFocus(element, false);
    }

    private static void OnHelpKey(object sender, KeyEventArgs args)
    {
        var window = (Window)sender;
        if (window.GetValue(ActiveTipProperty) is ToolTip open)
        {
            open.IsOpen = false;
            if (args.Key is Key.Escape or Key.F1) args.Handled = true;
            return;
        }
        if (args.Key != Key.F1) return;
        var origin = args.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject;
        for (var target = origin; target != null; target = ParentOf(target))
        {
            if (target is not FrameworkElement field || field.ToolTip is not string text || text.Length == 0) continue;
            var tip = new ToolTip { Content = text, Placement = PlacementMode.Bottom, PlacementTarget = field };
            if (window.TryFindResource(typeof(ToolTip)) is Style style) tip.Style = style;
            void Close(object? s, EventArgs e) => tip.IsOpen = false;
            void LostFocus(object s, KeyboardFocusChangedEventArgs e) => tip.IsOpen = false;
            window.Deactivated += Close;
            window.Closed += Close;
            field.LostKeyboardFocus += LostFocus;
            tip.Closed += (_, _) =>
            {
                window.Deactivated -= Close;
                window.Closed -= Close;
                field.LostKeyboardFocus -= LostFocus;
                window.ClearValue(ActiveTipProperty);
            };
            window.SetValue(ActiveTipProperty, tip);
            tip.IsOpen = true;
            args.Handled = true;
            break;
        }
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual visual ? VisualTreeHelper.GetParent(visual) ?? LogicalTreeHelper.GetParent(element)
                                 : LogicalTreeHelper.GetParent(element);

    // The same sun and two panel outlines as the Angle Studio wordmark.
    internal static DrawingImage MakeLogo()
    {
        var stroke = new Pen(Gold, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 32, 32));
            dc.DrawEllipse(null, stroke, new Point(23, 8), 4, 4);
            dc.DrawGeometry(null, stroke, Geometry.Parse("M3,19 L18,12 29,17 14,25 Z M4,25 L14,30 28,23"));
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
