using System.Runtime.InteropServices;
using WindowVeil.Geometry;
using WindowVeil.Platform;

namespace WindowVeil.UiTests;

/// <summary>
/// Minimal window snapshot helper for tests (the Automation module has no window system of its own). All calls run
/// with a temporary per-monitor v2 DPI awareness context so the rectangles are physical pixels, like UIA's.
/// TODO(lead): switch to the tracking module's Win32WindowSystem once merged.
/// </summary>
internal static partial class Win32Window
{
    private static readonly nint PerMonitorAwareV2 = -4;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

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

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    /// <summary>Whether the handle is a live window.</summary>
    public static bool Exists(nint hwnd) => hwnd != 0 && IsWindow(hwnd);

    /// <summary>
    /// Builds a <see cref="TargetWindowInfo"/> for <paramref name="hwnd"/>. For a dead handle the rectangles are empty
    /// (useful to simulate a stale context).
    /// </summary>
    public static TargetWindowInfo Snapshot(nint hwnd, string title = "")
    {
        nint previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            PixelRect bounds = PixelRect.Empty, client = PixelRect.Empty;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0)
                bounds = PixelRect.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
            else if (GetWindowRect(hwnd, out var wr))
                bounds = PixelRect.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);

            var origin = new POINT();
            if (GetClientRect(hwnd, out var cr) && ClientToScreen(hwnd, ref origin))
                client = new PixelRect(origin.X, origin.Y, cr.Right - cr.Left, cr.Bottom - cr.Top);

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            uint dpi = GetDpiForWindow(hwnd);
            return new TargetWindowInfo
            {
                Handle = hwnd,
                ProcessId = (int)pid,
                Title = title,
                Bounds = bounds,
                ClientBounds = client,
                IsVisible = IsWindowVisible(hwnd),
                IsMinimized = IsIconic(hwnd),
                IsForeground = GetForegroundWindow() == hwnd,
                Dpi = dpi == 0 ? DpiScale.Default : new DpiScale(dpi),
            };
        }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }
}
