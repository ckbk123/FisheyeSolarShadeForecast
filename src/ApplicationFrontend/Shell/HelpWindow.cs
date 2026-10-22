using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SolarShade.Desktop;

/// <summary>A reusable reference window that leaves the study available for interaction.</summary>
public sealed class HelpWindow : Window
{
    private HelpWindow(Window owner, string title, string text, Action<StackPanel>? addControls)
    {
        Title = title; Width = 740; Height = 700;
        WindowWorkArea.FitToOwner(this, owner);
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(MainWindow.Label("Keep this guide open while using the application.", 12));
        panel.Children.Add(new Separator { Margin = new(0, 10, 0, 10) });
        panel.Children.Add(MainWindow.Label(text, 14));
        addControls?.Invoke(panel);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public static void Open(ref HelpWindow? current, Window owner, string title, string text, Action<StackPanel>? addControls = null)
    {
        if (current is { IsVisible: true })
        {
            if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal;
            if (current.ShowActivated) current.Activate(); return;
        }
        current = new(owner, title, text, addControls);
        current.Show();
    }
}
