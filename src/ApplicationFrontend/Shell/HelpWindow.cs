using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SolarShade.Desktop;

/// <summary>A reusable reference window that leaves the study available for interaction.</summary>
public sealed class HelpWindow : Window
{
    private HelpWindow(Window owner, string title, string? text, Action<StackPanel>? addControls)
    {
        Title = title; Width = 740; Height = 700;
        WindowWorkArea.FitToOwner(this, owner);
        var panel = new StackPanel { Margin = new(22) };
        if (addControls != null || owner is MainWindow)
        {
            panel.Children.Add(MainWindow.Label("Hover over a control or focus it and press F1 for its explanation. Press Esc to dismiss.", 13));
            panel.Children.Add(new Separator { Margin = new(0, 10, 0, 10) });
        }
        if (text != null) panel.Children.Add(MainWindow.Label(text, 14));
        addControls?.Invoke(panel);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    public static void OpenGuide(ref HelpWindow? current, Window owner, string title,
        IReadOnlyList<(string Title, string Text)> topics, Action<StackPanel>? addControls = null)
    {
        Open(ref current, owner, title, null, panel =>
        {
            for (int i = 0; i < topics.Count; i++)
            {
                var topic = topics[i];
                var section = new Expander { Header = topic.Title, Margin = new(0, 2, 0, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                section.Expanded += (_, _) =>
                {
                    if (section.Content != null) return;
                    var body = MainWindow.Label(topic.Text, 14);
                    body.Margin = new(21, 6, 0, 8);
                    section.Content = body;
                };
                panel.Children.Add(section);
                section.IsExpanded = i == 0;
            }
            addControls?.Invoke(panel);
        });
    }

    public static void Open(ref HelpWindow? current, Window owner, string title, string? text, Action<StackPanel>? addControls = null)
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
