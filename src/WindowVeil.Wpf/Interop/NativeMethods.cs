using System.Runtime.InteropServices;

namespace WindowVeil.Wpf.Interop;

/// <summary>
/// The few user32 functions the WPF renderer needs. Kept internal so the package does not depend on
/// <c>WindowVeil.Native</c>.
/// </summary>
internal static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;

    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public const uint GW_HWNDPREV = 3;

    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_MOUSEACTIVATE = 0x0021;

    // MINMAXINFO = { POINT ptReserved; POINT ptMaxSize; POINT ptMaxPosition; POINT ptMinTrackSize; POINT ptMaxTrackSize; }
    public const int MINMAXINFO_MinTrackX = 24;
    public const int MINMAXINFO_MinTrackY = 28;
    public const int MINMAXINFO_MaxTrackX = 32;
    public const int MINMAXINFO_MaxTrackY = 36;
    public const int WM_DPICHANGED = 0x02E0;
    public const int MA_NOACTIVATE = 3;

    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_TOPMOST = -1;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint BeginDeferWindowPos(int nNumWindows);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint DeferWindowPos(nint hWinPosInfo, nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndDeferWindowPos(nint hWinPosInfo);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    /// <summary>Reads GWL_EXSTYLE on both 32- and 64-bit processes.</summary>
    public static long GetExStyle(nint hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE) : GetWindowLong32(hwnd, GWL_EXSTYLE);

    /// <summary>Writes GWL_EXSTYLE on both 32- and 64-bit processes.</summary>
    public static void SetExStyle(nint hwnd, long exStyle)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, (nint)exStyle);
        else SetWindowLong32(hwnd, GWL_EXSTYLE, unchecked((int)exStyle));
    }

    /// <summary>
    /// Switches the calling thread to per-monitor-v2 DPI awareness. Returns false on Windows versions that predate
    /// the API (before Windows 10 1607) or when the call is rejected.
    /// </summary>
    public static bool TrySetThreadPerMonitorV2()
    {
        try { return SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>Returns the window rectangle, or null when the handle is invalid.</summary>
    public static Geometry.PixelRect? TryGetWindowRect(nint hwnd) =>
        GetWindowRect(hwnd, out var r) ? Geometry.PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : null;
}
