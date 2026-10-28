using System.Runtime.InteropServices;
using WindowVeil.Geometry;

namespace WindowVeil.WpfTests.Infrastructure;

/// <summary>Win32 helpers for assertions. Every geometry query runs in a per-monitor-v2 scope so it sees physical pixels.</summary>
internal static partial class TestNative
{
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    private const int GWL_EXSTYLE = -20;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref POINT point);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr64(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong32(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>Runs <paramref name="func"/> with the calling thread temporarily per-monitor-v2 DPI aware.</summary>
    public static T PerMonitor<T>(Func<T> func)
    {
        nint previous = 0;
        try { previous = SetThreadDpiAwarenessContext(-4); }
        catch (EntryPointNotFoundException) { }
        try { return func(); }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    public static PixelRect GetWindowBounds(nint hwnd) => PerMonitor(() =>
        GetWindowRect(hwnd, out var r) ? PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : PixelRect.Empty);

    public static PixelRect GetClientBounds(nint hwnd) => PerMonitor(() =>
    {
        if (!GetClientRect(hwnd, out var r)) return PixelRect.Empty;
        var p = new POINT();
        ClientToScreen(hwnd, ref p);
        return new PixelRect(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
    });

    public static PixelRect GetMonitorBounds(nint hwnd) => PerMonitor(() =>
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)
            ? PixelRect.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom)
            : PixelRect.Empty;
    });

    public static nint WindowAt(PixelPoint point) => PerMonitor(() => WindowFromPoint(new POINT { X = point.X, Y = point.Y }));

    public static long GetExStyle(nint hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE) : GetWindowLong32(hwnd, GWL_EXSTYLE);

    public static DpiScale GetDpi(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return new DpiScale(dpi == 0 ? 96 : dpi);
    }
}
