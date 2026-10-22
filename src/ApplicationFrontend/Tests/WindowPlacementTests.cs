using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using SolarShade.Desktop;
using Xunit;
using static ManualUpdateTests;

[Collection("Manual update UI")]
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

    [Fact]
    public void SmallerHigherDpiMonitorReclampsMinimumsAndRestoresDesignMinimumOnReturn()
    {
        var design = new Size(1080, 720);
        var large = new Rect(0, 0, 2560, 1392);
        var small = new Rect(-1366, -100, 1366, 720);
        Assert.Equal(design, WindowWorkArea.MinimumForWorkArea(large, 1, design));
        var minimum = WindowWorkArea.MinimumForWorkArea(small, 1.5, design);
        Assert.Equal(1366 / 1.5, minimum.Width); Assert.Equal(480, minimum.Height);
        var fitted = WindowWorkArea.ConstrainedBounds(small, new(-1200, -50, 1440 * 1.5, 960 * 1.5));
        Assert.Equal(small, fitted);
        Assert.Equal(design, WindowWorkArea.MinimumForWorkArea(large, 1, design));
    }

    [Fact]
    public void FittingBoundsKeepsAnOrdinaryPositionAndOnlyMovesOverflow()
    {
        var work = new Rect(2560, -564, 1440, 2512);
        var current = new Rect(2700, -300, 1000, 800);
        Assert.Equal(current, WindowWorkArea.ConstrainedBounds(work, current));
        Assert.Equal(new Rect(3000, 1148, 1000, 800),
            WindowWorkArea.ConstrainedBounds(work, new(3500, 1800, 1000, 800)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualMainWindowRemainsOnItsTargetAfterLoadedAndRendered(bool ensureHandleFirst) => Sta(async () =>
    {
        using var fixture = new Fixture(); using var service = new AppServices(fixture.Debug);
        var window = new MainWindow(loadExampleOnFirstRun: false, applicationServices: service) { ShowActivated = false };
        bool loaded = false, rendered = false;
        window.Loaded += (_, _) => loaded = true; window.ContentRendered += (_, _) => rendered = true;
        try
        {
            if (ensureHandleFirst) new WindowInteropHelper(window).EnsureHandle();
            window.Show(); await Until(() => loaded && rendered); window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Assert.True(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info));
            Assert.True(GetWindowRect(handle, out var actual));
            Assert.True(actual.Left >= info.Work.Left && actual.Top >= info.Work.Top);
            Assert.True(actual.Right <= info.Work.Right && actual.Bottom <= info.Work.Bottom);
            if (Environment.GetEnvironmentVariable("SOLARSHADE_VALIDATION_MONITOR") == "secondary")
                Assert.Equal(0u, info.Flags & 1u);
            Assert.False(window.ShowActivated);
            Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
        }
        finally { window.Close(); await window.InputsSettled; }
    });

    [Fact]
    public void OrdinaryMoveWithinTheSecondaryDoesNotRecenterWindow() => Sta(async () =>
    {
        var window = new Window { Width = 600, Height = 500, MinWidth = 300, MinHeight = 250, ShowActivated = false };
        WindowWorkArea.FitOnStartup(window);
        try
        {
            window.Show(); var handle = new WindowInteropHelper(window).Handle;
            Assert.True(GetWindowRect(handle, out var initial));
            Assert.True(SetWindowPos(handle, IntPtr.Zero, initial.Left + 24, initial.Top + 24,
                initial.Right - initial.Left, initial.Bottom - initial.Top, 0x0014));
            await Task.Delay(50);
            Assert.True(GetWindowRect(handle, out var actual));
            Assert.Equal(initial.Left + 24, actual.Left); Assert.Equal(initial.Top + 24, actual.Top);
        }
        finally { window.Close(); }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr handle, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
