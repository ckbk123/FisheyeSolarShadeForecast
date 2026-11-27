using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SolarShade.Desktop;

/// <summary>Interactive equipment orientation map. The browser is created only when this tab is first displayed.</summary>
public sealed class OrientationWorkspace : UserControl, IDisposable
{
    private Task<CoreWebView2Environment>? environmentTask;
    private WebView2? browser;
    private readonly Func<EquipmentMapAngles, bool>? apply;
    private bool started, disposed;
    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolarShade", "OrientationMap");

    public OrientationWorkspace() : this(null) { }

    internal OrientationWorkspace(Func<EquipmentMapAngles, bool>? applyAngles)
    {
        apply = applyAngles;
        Loaded += (_, _) => StartIfVisible();
        IsVisibleChanged += (_, _) => StartIfVisible();
    }

    private Task<CoreWebView2Environment> GetEnvironment() =>
        environmentTask ??= CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(CacheRoot, "Browser"));

    // Hovering the tab gives the runtime a head start without creating a hidden browser.
    internal async Task Prepare()
    {
        if (disposed || started) return;
        try { await GetEnvironment(); }
        catch { environmentTask = null; } // Opening the tab retries and shows the usual fallback if necessary.
    }

    private async void StartIfVisible()
    {
        if (started || disposed || !IsLoaded || !IsVisible) return;
        started = true;
        try
        {
            var loading = new TextBlock { Text = "Opening orientation map…", Margin = new(12) };
            var view = new WebView2 { Visibility = Visibility.Hidden };
            browser = view;
            var surface = new Grid();
            surface.Children.Add(loading);
            surface.Children.Add(view);
            Content = surface;
            var environment = await GetEnvironment();
            if (disposed) return;
            await view.EnsureCoreWebView2Async(environment);
            if (disposed) return;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = apply != null;
            core.WebMessageReceived += (_, e) =>
            {
                if (disposed || apply == null || !OrientationMapAssets.IsPage(e.Source)) return;
                if (EquipmentMapAngles.Read(e.WebMessageAsJson) is not { } angles) return;
                core.PostWebMessageAsString(apply(angles) ? "Angles applied." : "Finish the current operation, then apply the angles again.");
            };
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) => e.Cancel = !OrientationMapAssets.IsPage(e.Uri);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                var content = OrientationMapAssets.IsPage(e.Request.Uri) ? OrientationMapAssets.OpenEmbeddedPage() : null;
                e.Response = content == null
                    ? environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                    : environment.CreateWebResourceResponse(content, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
            core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                    Dispatcher.BeginInvoke(() => ShowFallback("The embedded viewer stopped."));
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (disposed) return;
                if (e.IsSuccess)
                {
                    view.Visibility = Visibility.Visible;
                    surface.Children.Remove(loading);
                }
                else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    Dispatcher.BeginInvoke(() => ShowFallback("The embedded viewer could not load the map."));
            };
            core.Navigate(OrientationMapAssets.PageUrl);
        }
        catch (WebView2RuntimeNotFoundException) { ShowFallback("The Microsoft Edge WebView2 Runtime is unavailable."); }
        catch (Exception ex) { ShowFallback("The embedded viewer could not start. " + ex.Message); }
    }

    private void ShowFallback(string message)
    {
        if (disposed) return;
        var panel = new StackPanel { Margin = new(24), MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "Orientation map", FontSize = 20, FontWeight = FontWeights.SemiBold });
        var detail = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
        panel.Children.Add(detail);
        var open = new Button { Content = "Open map in browser", HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) =>
        {
            try
            {
                var folder = Path.Combine(CacheRoot, "Standalone", typeof(OrientationWorkspace).Module.ModuleVersionId.ToString("N"));
                var index = OrientationMapAssets.WriteStandalone(folder);
                Process.Start(new ProcessStartInfo(index) { UseShellExecute = true });
            }
            catch (Exception ex) { detail.Text = "Could not open the map. " + ex.Message; }
        };
        panel.Children.Add(open);
        Content = panel;
        browser?.Dispose();
        browser = null;
    }

    public void Dispose()
    {
        disposed = true;
        browser?.Dispose();
        browser = null;
    }
}
