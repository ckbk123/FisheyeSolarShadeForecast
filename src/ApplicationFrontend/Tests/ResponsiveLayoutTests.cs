using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class ResponsiveLayoutTests
{
    [Theory]
    [InlineData(1440, 960)]
    [InlineData(1280, 680)]
    [InlineData(1024, 544)]
    [InlineData(853, 453)]
    [InlineData(640, 340)]
    [InlineData(480, 320)]
    public void IrradiancePlotAndUpdateRemainReachableAfterShrinking(double width, double height) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            window.Show(); window.Calculate(); await Until(() => !window.UpdatingForTest);
            var completed = window.Completed; Assert.NotNull(completed);
            window.Width = width; window.Height = height; window.UpdateLayout();
            AssertActionsVisible(window.Irradiance.Actions, window);
            var chart = Descendants<IrradianceChart>(window).Single();
            chart.BringIntoView(); await Task.Delay(30); window.UpdateLayout();
            AssertVisibleInsideWorkspace(chart, window.Irradiance);
            Assert.True(chart.ActualHeight >= 180);
            var bitmap = new RenderTargetBitmap((int)chart.ActualWidth, (int)chart.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chart);
            Capture(window, $"irradiance-{width}-{height}");
            foreach (string id in new[] { "UpdateResults", "RefreshData" })
            {
                var button = Descendants<Button>(window.Irradiance).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == id);
                button.BringIntoView(); await Task.Delay(30); window.UpdateLayout();
                AssertVisibleInsideWorkspace(button, window.Irradiance);
            }
            foreach (string property in new[] { "Latitude", "Start", "PanelTilt", "PanelAzimuth" })
            {
                var field = Descendants<TextBox>(window.Irradiance).Single(b => Equals(b.Tag, property));
                field.BringIntoView(); await Task.Delay(30); window.UpdateLayout();
                AssertVisibleInsideWorkspace(field, window.Irradiance);
            }
            var diffuse = Descendants<CheckBox>(window.Irradiance).Single(c => Equals(c.Content, "Use isotropic diffuse instead of Hay–Davies"));
            diffuse.BringIntoView(); await Task.Delay(30); window.UpdateLayout();
            AssertVisibleInsideWorkspace(diffuse, window.Irradiance);
            Capture(window, $"historical-data-{width}-{height}");
            Assert.Same(completed, window.Completed); Assert.True(window.ExportEnabledForTest);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Theory]
    [InlineData(1440, 960)]
    [InlineData(1280, 680)]
    [InlineData(1024, 544)]
    [InlineData(853, 453)]
    [InlineData(640, 340)]
    [InlineData(480, 320)]
    public void PvPlotAndSystemControlsRemainReachableAfterShrinking(double width, double height) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            window.Show(); window.Calculate(); await Until(() => !window.UpdatingForTest);
            Assert.True(window.Workspaces.Select("pv"));
            var pv = window.PvAutonomy;
            pv.Controller.SetDraft(PvSettingsDraft.Example()); await pv.Controller.EvaluateAsync();
            Assert.True(pv.Controller.IsCurrent);
            var result = pv.Controller.Result;
            window.Width = width; window.Height = height; window.UpdateLayout();
            AssertActionsVisible(pv.Actions, window);
            pv.Chart.BringIntoView(); await Task.Delay(30); window.UpdateLayout();
            AssertVisibleInsideWorkspace(pv.Chart, pv); Assert.True(pv.Chart.ActualHeight >= 140);
            Capture(window, $"pv-{width}-{height}");
            var evaluate = Descendants<Button>(pv).Single(b => Equals(b.Content, "Evaluate system"));
            evaluate.BringIntoView(); await Task.Delay(30); window.UpdateLayout();
            AssertVisibleInsideWorkspace(evaluate, pv);
            Assert.Same(result, pv.Controller.Result); Assert.True(pv.Controller.IsCurrent);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    private static void AssertVisibleInsideWorkspace(FrameworkElement element, FrameworkElement workspace)
    {
        Check(workspace);
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent != null && parent != workspace; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollContentPresenter viewport) Check(viewport);
        void Check(FrameworkElement viewport)
        {
            var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
            // A chart can be taller than a very short high-DPI desktop. Its finite canvas must
            // remain scrollable and intersect the visible area; ordinary controls fit completely.
            bool tallChart = element is IrradianceChart or PvSystemChart && bounds.Height > viewport.ActualHeight;
            Assert.True(tallChart ? bounds.Top < viewport.ActualHeight && bounds.Bottom > 0
                : bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
                $"{element.GetType().Name} vertical bounds {bounds} exceed {viewport.GetType().Name} height {viewport.ActualHeight}.");
            Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1,
                $"{element.GetType().Name} horizontal bounds {bounds} exceed {viewport.GetType().Name} width {viewport.ActualWidth}.");
        }
    }
    private static void AssertActionsVisible(FrameworkElement actions, Window window)
    {
        foreach (var button in Descendants<Button>(actions))
        {
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= window.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
                $"Header action {button.Content} is clipped: {bounds} in {window.ActualWidth} × {window.ActualHeight}.");
        }
    }

    [Theory]
    [InlineData(1280, 680, 1)]
    [InlineData(1280, 680, 1.25)]
    [InlineData(1280, 680, 1.5)]
    [InlineData(1280, 680, 2)]
    public void PanesReflowWithinScaled720pWorkArea(double physicalWidth, double physicalHeight, double scale) => Sta(() =>
    {
        // A deterministic logical-size simulation; the user's monitor scaling is never changed.
        var sidebar = new Border { Child = new ScrollViewer { Content = new Border { Height = 1400 } } };
        var result = new Grid(); result.RowDefinitions.Add(new() { Height = GridLength.Auto }); result.RowDefinitions.Add(new());
        var commands = new WrapPanel();
        for (int i = 0; i < 8; i++) commands.Children.Add(new Button { Content = "Study command", Width = 130, Height = 34 });
        result.Children.Add(commands);
        var plot = new IrradianceChart(); Grid.SetRow(plot, 1); result.Children.Add(plot);
        var surface = new Grid(); surface.Children.Add(sidebar); surface.Children.Add(result);
        var viewport = new WorkspaceViewport(surface, sidebar, result);
        double width = physicalWidth / scale - 48, height = physicalHeight / scale - 150;
        for (int i = 0; i < 3; i++)
        {
            viewport.Measure(new Size(width, height)); viewport.Arrange(new Rect(0, 0, width, height)); viewport.UpdateLayout();
        }
        Assert.True(viewport.ScrollableWidth < 1);
        Assert.True(double.IsFinite(plot.ActualHeight) && plot.ActualHeight >= 180);
        Assert.True(double.IsFinite(plot.ActualWidth) && plot.ActualWidth > 0);
        Assert.Equal(width < 862 ? 2 : 0, Grid.GetRow(result));
        Assert.True(result.ActualHeight >= commands.ActualHeight + plot.ActualHeight);
        return Task.CompletedTask;
    });

    [Fact]
    public void OwnedDialogsFitTheOwnersScaledDesktop() => Sta(() =>
    {
        var owner = new Window { Width = 640, Height = 340, ShowActivated = false };
        _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
        var dialog = new Window { Width = 1000, Height = 700, MinWidth = 760, MinHeight = 540 };
        WindowWorkArea.FitToOwner(dialog, owner);
        Assert.Equal(640, dialog.Width); Assert.Equal(340, dialog.Height);
        Assert.True(dialog.MinWidth <= dialog.Width && dialog.MinHeight <= dialog.Height);
        Assert.Same(owner, dialog.Owner); Assert.False(dialog.ShowActivated);
        Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
        dialog.Close(); owner.Close();
        return Task.CompletedTask;
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("SOLARSHADE_LAYOUT_CAPTURE_DIR") is not { } folder) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
    }
}
