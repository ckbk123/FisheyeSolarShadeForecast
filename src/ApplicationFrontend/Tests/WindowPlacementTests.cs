using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

public sealed class WindowPlacementTests
{
    [Theory]
    [InlineData(0, 0, 1366, 728, 1)]
    [InlineData(0, 40, 1280, 680, 1.5)]
    [InlineData(-1920, -200, 1920, 1040, 1.25)]
    [InlineData(2560, 60, 2560, 1380, 2)]
    public void StartupBoundsFitTaskbarAndScaledMonitor(double x, double y, double width, double height, double scale)
    {
        var work = new Rect(x, y, width, height);
        var bounds = WindowWorkArea.CenteredBounds(work, new(1440 * scale, 960 * scale));
        Assert.True(work.Contains(bounds));
        Assert.Equal(work.Left + work.Width / 2, bounds.Left + bounds.Width / 2);
        Assert.Equal(work.Top + work.Height / 2, bounds.Top + bounds.Height / 2);
    }

    [Fact]
    public void FittingARealWpfWindowKeepsMinimumsWithinItsWorkArea() => Sta(() =>
    {
        var window = new Window { Width = 4000, Height = 4000, MinWidth = 3000, MinHeight = 3000 };
        WindowWorkArea.FitOnStartup(window);
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.True(window.Width < 4000); Assert.True(window.Height < 4000);
            Assert.True(window.MinWidth <= window.Width); Assert.True(window.MinHeight <= window.Height);
            var handle = new WindowInteropHelper(window).Handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.True(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info));
            Assert.True(GetWindowRect(handle, out var actual));
            Assert.True(actual.Left >= info.Work.Left && actual.Top >= info.Work.Top);
            Assert.True(actual.Right <= info.Work.Right && actual.Bottom <= info.Work.Bottom);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
}
