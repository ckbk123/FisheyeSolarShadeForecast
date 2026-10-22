using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SolarShade.Desktop;

/// <summary>Keeps a new window, including its title bar, inside its monitor's usable desktop.</summary>
public static class WindowWorkArea
{
    public static Rect CenteredBounds(Rect workArea, Size preferred)
    {
        double width = Math.Min(preferred.Width, workArea.Width);
        double height = Math.Min(preferred.Height, workArea.Height);
        return new(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
    }

    public static void FitOnStartup(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = MonitorFromWindow(handle, 2 /* nearest */);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            double scale = GetDpiForWindow(handle) / 96d;
            if (scale <= 0) scale = 1;
            var work = new Rect(info.Work.Left, info.Work.Top,
                info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
            var bounds = CenteredBounds(work, new(window.Width * scale, window.Height * scale));
            window.MinWidth = Math.Min(window.MinWidth, work.Width / scale);
            window.MinHeight = Math.Min(window.MinHeight, work.Height / scale);
            window.Width = bounds.Width / scale; window.Height = bounds.Height / scale;
            // Native desktop coordinates also handle monitors left/above the primary at a different DPI.
            SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
                (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0014 /* no Z order or activation */);
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
