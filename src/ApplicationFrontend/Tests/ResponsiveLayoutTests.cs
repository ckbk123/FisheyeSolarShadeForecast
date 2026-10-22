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
    [InlineData(1080, 720)]
    [InlineData(800, 600)]
    [InlineData(760, 540)]
    public void IrradiancePlotAndUpdateRemainReachableAfterShrinking(double width, double height) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            window.Show(); window.Calculate(); await Until(() => !window.UpdatingForTest);
            var completed = window.Completed; Assert.NotNull(completed);
            window.Width = width; window.Height = height; window.UpdateLayout();
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
            foreach (string property in new[] { "Latitude", "Start" })
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
    [InlineData(1080, 720)]
    [InlineData(800, 600)]
    [InlineData(760, 540)]
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
            Assert.True(bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
                $"{element.GetType().Name} vertical bounds {bounds} exceed {viewport.GetType().Name} height {viewport.ActualHeight}.");
            Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1,
                $"{element.GetType().Name} horizontal bounds {bounds} exceed {viewport.GetType().Name} width {viewport.ActualWidth}.");
        }
    }
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
