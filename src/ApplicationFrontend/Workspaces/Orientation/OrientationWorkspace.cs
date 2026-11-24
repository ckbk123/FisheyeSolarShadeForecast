using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SolarShade.Desktop;

/// <summary>An isolated illustration. The browser is created only when this tab is first displayed.</summary>
public sealed class OrientationWorkspace : UserControl, IDisposable
{
    private WebView2? browser;
    private bool started, disposed;
    private static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolarShade", "OrientationMap");

    public OrientationWorkspace()
    {
        Loaded += (_, _) => StartIfVisible();
        IsVisibleChanged += (_, _) => StartIfVisible();
    }

    private async void StartIfVisible()
    {
        if (started || disposed || !IsLoaded || !IsVisible) return;
        started = true;
        try
        {
            Content = new TextBlock { Text = "Opening orientation map…", Margin = new(24) };
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(CacheRoot, "Browser"));
            if (disposed) return;
            var view = new WebView2();
            browser = view;
            Content = view;
            await view.EnsureCoreWebView2Async(environment);
            if (disposed) return;
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) => e.Cancel = !OrientationMapAssets.IsPage(e.Uri);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                var content = OrientationMapAssets.Open(e.Request.Uri, out string type);
                e.Response = content == null
                    ? environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                    : environment.CreateWebResourceResponse(content, 200, "OK", $"Content-Type: {type}; charset=utf-8");
            };
            core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                    Dispatcher.BeginInvoke(() => ShowFallback("The embedded viewer stopped."));
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
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
