using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
public sealed class FixedUpdateControlsTests
{
    [Theory]
    [InlineData(1440, 960)]
    [InlineData(1280, 680)]
    [InlineData(853, 453)]
    [InlineData(640, 340)]
    [InlineData(480, 320)]
    public void UpdateCommandsStayVisibleAndStationaryWhileStudyPanesScroll(double width, double height) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service);
        try
        {
            var surface = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            await Layout(surface, width, height);
            var block = Find<Border>(surface, "IrradianceUpdateControls");
            var inputs = Find<ScrollViewer>(surface, "IrradianceInputs");
            var viewport = Assert.Single(Descendants<WorkspaceViewport>(window.Irradiance));
            var chart = Assert.Single(Descendants<IrradianceChart>(window.Irradiance));
            var indicator = Find<TextBlock>(block, "IrradianceUpdateState");
            var buttons = new[] { "UpdateResults", "StopUpdate", "RefreshData" }
                .Select(id => Find<Button>(block, id)).ToArray();

            Assert.Empty(Ancestors<ScrollViewer>(block));
            Assert.Contains(viewport, Ancestors<WorkspaceViewport>(inputs));
            Assert.Equal(348, block.MaxWidth);
            Assert.Equal(FontWeights.Bold, indicator.FontWeight);
            Assert.Contains(Descendants<TextBlock>(block), text => text.Text == "UPDATE RESULTS" && text.FontWeight == FontWeights.Bold);
            Assert.All(buttons, button => Assert.Equal(FontWeights.Bold, button.FontWeight));
            var footer = Assert.Single(Descendants<ScrollViewer>(Assert.IsType<Grid>(VisualTreeHelper.GetParent(block))));
            Assert.Equal(80, footer.MaxHeight); Assert.DoesNotContain(footer, Ancestors<ScrollViewer>(block));
            Assert.True(Bounds(block, surface).Top >= Bounds(viewport, surface).Bottom - 1,
                "Update commands must remain below the scrollable study panes.");

            foreach (string property in new[] { "PanelTilt", "PanelAzimuth" })
            {
                var field = Assert.Single(Descendants<TextBox>(inputs), box => Equals(box.Tag, property));
                Assert.Contains(inputs, Ancestors<ScrollViewer>(field));
            }
            Assert.Contains(Descendants<TextBlock>(inputs), text => text.Text.StartsWith("Update results reuses matching weather data.", StringComparison.Ordinal));

            FrameworkElement[] fixedElements = [block, indicator, .. buttons];
            var originalBounds = fixedElements.ToDictionary(element => element, element => Bounds(element, surface));
            AssertFixed("initial layout");
            Capture(window, surface, width, height);

            Assert.True(inputs.ScrollableHeight > 0, "The input pane must contain enough content to exercise scrolling.");
            inputs.ScrollToBottom(); await Layout(surface, width, height);
            Assert.True(inputs.VerticalOffset > 0); AssertFixed("input bottom");
            inputs.ScrollToTop(); await Layout(surface, width, height);
            Assert.InRange(inputs.VerticalOffset, 0, 1); AssertFixed("input top");

            viewport.ScrollToBottom(); await Layout(surface, width, height);
            if (viewport.ScrollableHeight > 1) Assert.True(viewport.VerticalOffset > 0);
            AssertFixed("study bottom");
            viewport.ScrollToTop(); await Layout(surface, width, height);
            Assert.InRange(viewport.VerticalOffset, 0, 1); AssertFixed("study top");

            chart.BringIntoView(); await Layout(surface, width, height); AssertFixed("chart BringIntoView");
            foreach (string property in new[] { "Latitude", "PanelAzimuth" })
            {
                Assert.Single(Descendants<TextBox>(inputs), box => Equals(box.Tag, property)).BringIntoView();
                await Layout(surface, width, height); AssertFixed(property + " BringIntoView");
            }
            Assert.False(window.IsVisible); Assert.False(window.UpdatingForTest); Assert.Null(window.Completed); Assert.Equal(0, fixture.Runs);

            void AssertFixed(string operation)
            {
                foreach (var element in fixedElements)
                {
                    var bounds = Bounds(element, surface); var expected = originalBounds[element];
                    string name = AutomationProperties.GetAutomationId(element);
                    Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, $"{name} has no visible size after {operation}.");
                    Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
                        $"{name} is clipped after {operation}: {bounds} in {width} × {height}.");
                    Assert.True(Math.Abs(bounds.Left - expected.Left) < 0.5 && Math.Abs(bounds.Top - expected.Top) < 0.5 &&
                        Math.Abs(bounds.Width - expected.Width) < 0.5 && Math.Abs(bounds.Height - expected.Height) < 0.5,
                        $"{name} moved after {operation}: {expected} → {bounds}.");
                }
            }
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    private static async Task Layout(FrameworkElement surface, double width, double height)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            surface.Measure(new Size(width, height)); surface.Arrange(new Rect(0, 0, width, height)); surface.UpdateLayout();
            await Task.Delay(1);
        }
    }

    private static Rect Bounds(FrameworkElement element, Visual relativeTo) =>
        element.TransformToAncestor(relativeTo).TransformBounds(new Rect(element.RenderSize));

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Assert.Single(Descendants<T>(root), element => AutomationProperties.GetAutomationId(element) == id);

    private static IEnumerable<T> Ancestors<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current != null; current = VisualTreeHelper.GetParent(current))
            if (current is T ancestor) yield return ancestor;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, FrameworkElement surface, double width, double height)
    {
        string? folder = Environment.GetEnvironmentVariable("SOLARSHADE_FIXED_UPDATE_QA");
        if (string.IsNullOrWhiteSpace(folder) || width is not (1440 or 640)) return;
        var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        image.Render(background); image.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(folder);
        using var output = File.Create(Path.Combine(folder, $"fixed-update-{width:0}-{height:0}.png")); encoder.Save(output);
    }
}
