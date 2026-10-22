using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SolarShade.Desktop;

/// <summary>Keeps startup and monitor transitions within the usable desktop, without recentering ordinary moves.</summary>
public static class WindowWorkArea
{
    public static Rect CenteredBounds(Rect workArea, Size preferred)
    {
        double width = Math.Min(preferred.Width, workArea.Width);
        double height = Math.Min(preferred.Height, workArea.Height);
        return new(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
    }

    public static Size MinimumForWorkArea(Rect workArea, double scale, Size designMinimum) =>
        new(Math.Min(designMinimum.Width, workArea.Width / scale), Math.Min(designMinimum.Height, workArea.Height / scale));

    public static Rect ConstrainedBounds(Rect workArea, Rect current)
    {
        double width = Math.Min(current.Width, workArea.Width), height = Math.Min(current.Height, workArea.Height);
        return new(Math.Clamp(current.Left, workArea.Left, workArea.Right - width),
            Math.Clamp(current.Top, workArea.Top, workArea.Bottom - height), width, height);
    }

    public static void FitOnStartup(Window window) => _ = new Placement(window);

    private sealed class Placement
    {
        private readonly Window window;
        private readonly Size preferred, designMinimum;
        private readonly bool secondaryValidation;
        private HwndSource? source;
        private IntPtr handle, lastMonitor;
        private Rect lastWorkArea;
        private double lastScale;
        private bool fitting, moving, queued, closed, forceCheck;

        public Placement(Window window)
        {
            this.window = window;
            preferred = new(window.Width, window.Height); designMinimum = new(window.MinWidth, window.MinHeight);
            secondaryValidation = Environment.GetEnvironmentVariable("SOLARSHADE_VALIDATION_MONITOR") == "secondary";
            if (secondaryValidation) window.ShowActivated = false;
            window.SourceInitialized += Initialize;
            window.LocationChanged += (_, _) => QueueCheck();
            window.DpiChanged += (_, _) => QueueCheck();
            window.StateChanged += (_, _) => { forceCheck = true; QueueCheck(); };
            window.Closed += (_, _) => { closed = true; source?.RemoveHook(Messages); };
        }

        private void Initialize(object? sender, EventArgs args)
        {
            handle = new WindowInteropHelper(window).Handle;
            source = HwndSource.FromHwnd(handle); source?.AddHook(Messages);
            var monitor = MonitorFromWindow(handle, 2 /* nearest */);
            if (secondaryValidation)
            {
                monitor = IntPtr.Zero;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr candidate, IntPtr dc, ref NativeRect rectangle, IntPtr data) =>
                {
                    var candidateInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(candidate, ref candidateInfo) && (candidateInfo.Flags & 1) == 0)
                    { monitor = candidate; return false; }
                    return true;
                }, IntPtr.Zero);
                if (monitor == IntPtr.Zero) throw new InvalidOperationException("Secondary-monitor validation requires a secondary display.");
            }
            if (WorkArea(monitor) is not { } work) return;
            fitting = true;
            try
            {
                // EnsureHandle can precede Show; later WPF startup centering must not undo native placement.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                if (MonitorFromWindow(handle, 2) != monitor)
                {
                    // Move the still-hidden HWND onto its target before asking for its DPI. A small rectangle
                    // avoids a large initial window remaining mostly on a different monitor.
                    window.MinWidth = window.MinHeight = 0;
                    SetWindowPos(handle, IntPtr.Zero, (int)(work.Left + work.Width / 2), (int)(work.Top + work.Height / 2), 100, 100, 0x0014);
                }
                double scale = Scale();
                Apply(work, scale, CenteredBounds(work, new(preferred.Width * scale, preferred.Height * scale)));
                Remember(monitor, work, scale);
            }
            finally { fitting = false; }
        }

        private IntPtr Messages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0231) moving = true; // WM_ENTERSIZEMOVE
            if (message == 0x0232) { moving = false; QueueCheck(); } // WM_EXITSIZEMOVE
            if (message is 0x007E or 0x001A) QueueCheck(); // display/work-area changes
            return IntPtr.Zero;
        }
        private void QueueCheck()
        {
            if (fitting || moving || queued || closed || handle == IntPtr.Zero) return;
            queued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                queued = false;
                if (closed || moving || window.WindowState == WindowState.Minimized) return;
                var monitor = MonitorFromWindow(handle, 2);
                if (WorkArea(monitor) is not { } work) return;
                double scale = Scale();
                if (!forceCheck && monitor == lastMonitor && work == lastWorkArea && scale == lastScale) return;
                forceCheck = false; fitting = true;
                try
                {
                    var minimum = MinimumForWorkArea(work, scale, designMinimum);
                    window.MinWidth = minimum.Width; window.MinHeight = minimum.Height;
                    if (window.WindowState == WindowState.Normal && GetWindowRect(handle, out var native))
                    {
                        var current = Bounds(native); var fitted = ConstrainedBounds(work, current);
                        if (fitted != current) Apply(work, scale, fitted);
                    }
                    Remember(monitor, work, scale);
                }
                finally { fitting = false; }
            });
        }
        private void Apply(Rect work, double scale, Rect bounds)
        {
            var minimum = MinimumForWorkArea(work, scale, designMinimum);
            window.MinWidth = minimum.Width; window.MinHeight = minimum.Height;
            window.Width = bounds.Width / scale; window.Height = bounds.Height / scale;
            SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
                (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0014 /* no Z order or activation */);
        }
        private double Scale() { uint dpi = GetDpiForWindow(handle); return dpi > 0 ? dpi / 96d : 1; }
        private void Remember(IntPtr monitor, Rect work, double scale)
        { lastMonitor = monitor; lastWorkArea = work; lastScale = scale; }
    }

    private static Rect? WorkArea(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? Bounds(info.Work) : null;
    }
    private static Rect Bounds(NativeRect value) => new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rectangle, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
