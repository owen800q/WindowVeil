using System.Runtime.InteropServices;
using WindowVeil.Geometry;

namespace WindowVeil.Editor.Services;

/// <summary>The few Win32 calls the editor needs itself (everything else goes through WindowVeil).</summary>
internal static partial class NativeMethods
{
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// The cursor position in PHYSICAL virtual-desktop pixels. The editor is Per-Monitor-V2 aware (app.manifest), so
    /// GetCursorPos is not virtualized and matches the coordinates used by UI Automation and WindowVeil.
    /// </summary>
    public static bool TryGetCursorPosition(out PixelPoint point)
    {
        if (GetCursorPos(out var p))
        {
            point = new PixelPoint(p.X, p.Y);
            return true;
        }
        point = default;
        return false;
    }
}
