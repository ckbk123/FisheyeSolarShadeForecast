using System.Windows;
using System.Windows.Controls;

namespace SolarShade.Desktop;

/// <summary>Keeps charts finite and lets controls remain reachable as the available desktop shrinks.</summary>
public sealed class WorkspaceViewport : ScrollViewer
{
    private readonly FrameworkElement surface;
    private readonly Grid? columns;
    private readonly FrameworkElement? sidebar, content;
    private readonly double sidebarWidth, contentMinimumWidth, sidebarMinimumHeight, contentMinimumHeight;
    private bool? stacked;
    private const double Gap = 14;

    public WorkspaceViewport(FrameworkElement surface)
    {
        this.surface = surface; Content = surface;
        VerticalScrollBarVisibility = HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        CanContentScroll = false;
        surface.Width = surface.MinWidth; surface.Height = surface.MinHeight;
        ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, this) && (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)) FitCanvas();
        };
        Loaded += (_, _) => FitCanvas();
    }

    public WorkspaceViewport(Grid surface, FrameworkElement sidebar, FrameworkElement content,
        double sidebarWidth = 348, double contentMinimumWidth = 500,
        double sidebarMinimumHeight = 600, double contentMinimumHeight = 640) : this(surface)
    {
        columns = surface; this.sidebar = sidebar; this.content = content;
        this.sidebarWidth = sidebarWidth; this.contentMinimumWidth = contentMinimumWidth;
        this.sidebarMinimumHeight = sidebarMinimumHeight; this.contentMinimumHeight = contentMinimumHeight;
        surface.MinWidth = Math.Min(sidebarWidth, contentMinimumWidth);
        surface.Height = double.NaN;
        content.MinHeight = Math.Max(content.MinHeight, contentMinimumHeight);
        FitCanvas();
    }

    private void FitCanvas()
    {
        double width = Math.Max(surface.MinWidth, Math.Max(0, ViewportWidth) - surface.Margin.Left - surface.Margin.Right);
        double height = Math.Max(0, ViewportHeight - surface.Margin.Top - surface.Margin.Bottom);
        surface.Width = width;
        if (columns == null)
        {
            surface.Height = Math.Max(surface.MinHeight, height);
            return;
        }
        bool narrow = width < sidebarWidth + Gap + contentMinimumWidth;
        if (narrow != stacked)
        {
            stacked = narrow;
            columns.ColumnDefinitions.Clear(); columns.RowDefinitions.Clear();
            if (narrow)
            {
                columns.ColumnDefinitions.Add(new());
                columns.RowDefinitions.Add(new() { Height = GridLength.Auto });
                columns.RowDefinitions.Add(new() { Height = new(Gap) });
                columns.RowDefinitions.Add(new() { Height = GridLength.Auto });
                Grid.SetRow(content!, 2); Grid.SetColumn(content!, 0);
            }
            else
            {
                columns.ColumnDefinitions.Add(new() { Width = new(sidebarWidth) });
                columns.ColumnDefinitions.Add(new() { Width = new(Gap) });
                columns.ColumnDefinitions.Add(new());
                columns.RowDefinitions.Add(new());
                Grid.SetRow(content!, 0); Grid.SetColumn(content!, 2);
            }
            Grid.SetRow(sidebar!, 0); Grid.SetColumn(sidebar!, 0);
        }
        // The sidebar has its own scroller. The result pane may grow naturally when labels
        // or toolbars wrap, while its chart still receives a finite arranged rectangle.
        sidebar!.Height = narrow ? sidebarMinimumHeight : Math.Max(sidebarMinimumHeight, height);
        surface.MinHeight = narrow ? sidebarMinimumHeight + Gap + contentMinimumHeight
            : Math.Max(height, Math.Max(sidebarMinimumHeight, contentMinimumHeight));
    }
}
