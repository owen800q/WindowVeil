using WindowVeil.Geometry;
using WindowVeil.Native.Interop;
using static WindowVeil.Native.Interop.NativeMethods;

namespace WindowVeil.Native;

/// <summary>
/// Low-level Win32 queries for diagnostics, leak tests and screenshot tooling. Coordinates are physical pixels
/// (each call runs in a per-monitor-v2 DPI context).
/// </summary>
public static class NativeDiagnostics
{
    /// <summary>Number of USER and GDI objects owned by the current process (<c>GetGuiResources</c>).</summary>
    public static (int UserObjects, int GdiObjects) GetGuiResourceCounts()
    {
        nint process = GetCurrentProcess();
        return ((int)GetGuiResources(process, GR_USEROBJECTS), (int)GetGuiResources(process, GR_GDIOBJECTS));
    }

    /// <summary>Window styles (<c>GWL_STYLE</c>).</summary>
    public static long GetWindowStyles(nint hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE) & 0xFFFFFFFFL;

    /// <summary>Extended window styles (<c>GWL_EXSTYLE</c>).</summary>
    public static long GetExtendedStyles(nint hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE) & 0xFFFFFFFFL;

    /// <summary>Window rectangle (<c>GetWindowRect</c>) in physical pixels, or empty when the call fails.</summary>
    public static PixelRect GetWindowRect(nint hwnd)
    {
        using var scope = DpiAwarenessScope.EnterPerMonitorV2();
        return NativeMethods.GetWindowRect(hwnd, out var r) ? r.ToPixelRect() : PixelRect.Empty;
    }

    /// <summary>Whether the handle identifies an existing window.</summary>
    public static bool IsWindow(nint hwnd) => NativeMethods.IsWindow(hwnd);

    /// <summary>Whether the window has the WS_VISIBLE style (and its ancestors too).</summary>
    public static bool IsWindowVisible(nint hwnd) => NativeMethods.IsWindowVisible(hwnd);

    /// <summary>The current foreground window.</summary>
    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>The window that hit-testing finds at a physical screen point.</summary>
    public static nint WindowFromPoint(PixelPoint point)
    {
        using var scope = DpiAwarenessScope.EnterPerMonitorV2();
        return NativeMethods.WindowFromPoint(new POINT(point.X, point.Y));
    }

    /// <summary>The window directly above <paramref name="hwnd"/> in z-order (<c>GW_HWNDPREV</c>), or 0.</summary>
    public static nint GetWindowAbove(nint hwnd) => NativeMethods.GetWindow(hwnd, GW_HWNDPREV);

    /// <summary>Layered window opacity (0..255), or null when the window has no LWA_ALPHA attribute.</summary>
    public static byte? GetLayeredWindowAlpha(nint hwnd) =>
        GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) && (flags & LWA_ALPHA) != 0 ? alpha : null;

    /// <summary>Waits until DWM has composed the next frame, so recent window changes are on screen. Returns false on failure.</summary>
    public static bool DwmFlush()
    {
        try { return NativeMethods.DwmFlush() >= 0; }
        catch (DllNotFoundException) { return false; }
    }
}
