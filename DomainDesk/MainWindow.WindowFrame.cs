using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DomainDesk;

public partial class MainWindow
{
    private readonly Size _preferredMinimum;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowBoundsHook);
    }

    private nint WindowBoundsHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        if (message != wmGetMinMaxInfo) return 0;
        var monitor = MonitorFromWindow(hwnd, 2); // Nearest monitor, including windows between displays.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return 0;

        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Win32 expects physical pixels relative to this monitor, not WPF DIPs or primary-screen bounds.
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        limits.MaxTrackSize.X = Math.Max(limits.MaxTrackSize.X, limits.MaxSize.X);
        limits.MaxTrackSize.Y = Math.Max(limits.MaxTrackSize.Y, limits.MaxSize.Y);
        Marshal.StructureToPtr(limits, lParam, false);
        FitMinimumToWorkArea(limits.MaxSize.X, limits.MaxSize.Y, GetDpiForWindow(hwnd));
        // Let WPF apply its minimum/maximum tracking sizes after correcting the maximize bounds.
        return 0;
    }

    private void FitMinimumToWorkArea(int width, int height, uint dpi)
    {
        var scale = dpi == 0 ? 1 : dpi / 96d;
        // A preferred minimum must not force maximized windows past a small or high-DPI work area.
        MinWidth = Math.Min(_preferredMinimum.Width, width / scale);
        MinHeight = Math.Min(_preferredMinimum.Height, height / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
