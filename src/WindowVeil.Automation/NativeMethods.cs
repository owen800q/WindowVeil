using System.Runtime.InteropServices;

namespace WindowVeil.Automation;

internal static partial class NativeMethods
{
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE.</summary>
    public static readonly nint DpiAwarenessContextPerMonitorAware = -3;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    public const uint GA_ROOT = 2;
    public const uint GW_HWNDNEXT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    public static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, ref POINT point);

    /// <summary>Top-level (root) window of <paramref name="hwnd"/>, or 0.</summary>
    public static nint GetRoot(nint hwnd) => hwnd == 0 ? 0 : GetAncestor(hwnd, GA_ROOT);
}
