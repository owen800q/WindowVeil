using System.Runtime.InteropServices;
using WindowVeil.Geometry;

namespace WindowVeil.RetroCapDemo;

/// <summary>Minimal Win32 helpers for driving the external RetroCap window in tests and demos.</summary>
internal static partial class Win32
{
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int SW_RESTORE = 9;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int cmd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    private static readonly nint PerMonitorAwareV2 = -4;

    private static T PerMonitor<T>(Func<T> f)
    {
        var old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try { return f(); }
        finally { if (old != 0) SetThreadDpiAwarenessContext(old); }
    }

    /// <summary>Visible frame bounds in physical pixels (DWM extended frame, falling back to GetWindowRect).</summary>
    public static PixelRect GetFrameBounds(nint hwnd) => PerMonitor(() =>
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hwnd, out r))
            return PixelRect.Empty;
        return PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    });

    /// <summary>Raw window rect in physical pixels.</summary>
    public static PixelRect GetWindowBounds(nint hwnd) => PerMonitor(() =>
        GetWindowRect(hwnd, out var r) ? PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : PixelRect.Empty);

    /// <summary>Moves a window (physical pixels) without activating it.</summary>
    public static void Move(nint hwnd, int x, int y) =>
        PerMonitor(() => SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE));

    /// <summary>Moves and resizes a window (physical pixels, raw window rect) without activating it.</summary>
    public static void SetBounds(nint hwnd, PixelRect bounds) =>
        PerMonitor(() => SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE));

    /// <summary>Restores and brings a window to the foreground (best effort).</summary>
    public static bool Activate(nint hwnd)
    {
        ShowWindow(hwnd, SW_RESTORE);
        return SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd;
    }

    /// <summary>The current foreground window.</summary>
    public static nint Foreground() => GetForegroundWindow();

    /// <summary>True when the window's thread processes messages within the timeout.</summary>
    public static bool IsResponsive(nint hwnd, TimeSpan timeout) =>
        SendMessageTimeout(hwnd, 0 /* WM_NULL */, 0, 0, SMTO_ABORTIFHUNG, (uint)timeout.TotalMilliseconds, out _) != 0;

    /// <summary>DPI of the window's monitor.</summary>
    public static uint GetDpi(nint hwnd) { var d = GetDpiForWindow(hwnd); return d == 0 ? 96 : d; }

    /// <summary>The top-level window under a screen point (physical).</summary>
    public static nint TopLevelWindowAt(PixelPoint p) => PerMonitor(() => GetAncestor(WindowFromPoint(new POINT { X = p.X, Y = p.Y }), 2 /* GA_ROOT */));

    /// <summary>
    /// Returns null when an interactive, unlocked desktop is available, otherwise the reason it is not.
    /// </summary>
    public static string? InteractiveDesktopProblem()
    {
        if (!OperatingSystem.IsWindows()) return "Not running on Windows.";
        if (!Environment.UserInteractive) return "The process is not running in an interactive user session.";
        var desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desk == 0) return "The input desktop cannot be opened (workstation locked, secure desktop or service session).";
        CloseDesktop(desk);
        return null;
    }
}
