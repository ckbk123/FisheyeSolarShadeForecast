using System.Windows;
using System.Windows.Controls;

namespace SolarShade.Desktop;

public sealed record WorkspaceDescriptor(string Id, string Title, FrameworkElement View, FrameworkElement? Actions = null);

/// <summary>Persistent workspace instances. Selection never creates services or starts scientific work.</summary>
public sealed class WorkspaceHost : TabControl, IDisposable
{
    private readonly Dictionary<string, TabItem> tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> availability = new(StringComparer.Ordinal);
    private bool disposed;
    public event EventHandler? ActiveChanged;
    public WorkspaceDescriptor? Active => (SelectedItem as TabItem)?.Tag as WorkspaceDescriptor;
    public WorkspaceHost()
    {
        BorderThickness = new(0); Background = System.Windows.Media.Brushes.Transparent;
        SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, this)) return;
            foreach (var pair in tabs) pair.Value.IsEnabled = availability[pair.Key] || ReferenceEquals(SelectedItem, pair.Value);
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        };
    }
    public void Register(WorkspaceDescriptor workspace)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(workspace.Id) || tabs.ContainsKey(workspace.Id)) throw new ArgumentException("Unique workspace ID required.");
        var tab = new TabItem { Header = workspace.Title, Content = workspace.View, Tag = workspace,
            Padding = new Thickness(18, 8, 18, 8) };
        tabs.Add(workspace.Id, tab); availability.Add(workspace.Id, true); Items.Add(tab);
        if (Items.Count == 1) SelectedItem = tab;
    }
    public void SetAvailability(string id, bool ready, string? reason)
    {
        var tab = tabs[id];
        availability[id] = ready;
        // An already open workspace remains visible so it can explain a newly invalid source.
        tab.IsEnabled = ready || ReferenceEquals(SelectedItem, tab);
        tab.ToolTip = ready ? null : reason;
        System.Windows.Automation.AutomationProperties.SetHelpText(tab, ready ? "" : reason ?? "Source unavailable.");
    }
    public bool Select(string id)
    {
        if (!tabs.TryGetValue(id, out var tab) || !tab.IsEnabled) return false;
        SelectedItem = tab; return true;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var item in tabs.Values.Reverse())
            if (((WorkspaceDescriptor)item.Tag).View is IDisposable resource) resource.Dispose();
        ActiveChanged = null;
    }
}
