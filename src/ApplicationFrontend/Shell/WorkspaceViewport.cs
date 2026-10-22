using System.Windows;
using System.Windows.Controls;

namespace SolarShade.Desktop;

/// <summary>Gives proportional layouts a finite canvas and scrolls it when the window is smaller.</summary>
public sealed class WorkspaceViewport : ScrollViewer
{
    private readonly FrameworkElement surface;
    public WorkspaceViewport(FrameworkElement surface)
    {
        this.surface = surface; Content = surface;
        VerticalScrollBarVisibility = HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        CanContentScroll = false;
        surface.Width = surface.MinWidth; surface.Height = surface.MinHeight;
        ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitCanvas();
        };
        Loaded += (_, _) => FitCanvas();
    }
    private void FitCanvas()
    {
        surface.Width = Math.Max(surface.MinWidth, ViewportWidth - surface.Margin.Left - surface.Margin.Right);
        surface.Height = Math.Max(surface.MinHeight, ViewportHeight - surface.Margin.Top - surface.Margin.Bottom);
    }
}
