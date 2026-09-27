using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SolarShade.Desktop;

public static class Program
{
    public static readonly Stopwatch Startup = Stopwatch.StartNew();
    [STAThread]
    public static int Main(string[] args)
    {
        Startup.Restart();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            try { File.AppendAllText(AppData.PathFor("errors.log"), DateTimeOffset.Now + " " + e.Exception + Environment.NewLine); } catch { }
            MessageBox.Show(e.Exception.Message, "SolarShade · something needs attention", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true;
        };
        if (args.Length >= 2 && args[0] == "--smoke-test") return SmokeTest.Run(app, args[1]);
        if (args.Length >= 2 && args[0] == "--portable-smoke-test") return SmokeTest.Run(app, args[1], portable: true);
        try { var window = new MainWindow(); app.Run(window); return 0; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n\nExtract the complete package to a folder you can write to, then open APPLICATION.exe there.", "SolarShade · unable to start", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
