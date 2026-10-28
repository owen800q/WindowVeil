using System.Runtime.InteropServices;

namespace WindowVeil.Capture.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly Geometry.PixelRect ToPixelRect() => Geometry.PixelRect.FromLTRB(Left, Top, Right, Bottom);
}

/// <summary>BITMAPINFOHEADER (40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

/// <summary>BITMAPINFO with a single (unused for 32bpp BI_RGB) color table entry.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    public uint bmiColors;
}

/// <summary>GDI/USER declarations for capture. BOOL is marshaled as 4-byte Bool; handles are nint.</summary>
internal static unsafe partial class CaptureNativeMethods
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";

    public const uint SRCCOPY = 0x00CC0020;
    /// <summary>Includes layered windows (such as WindowVeil overlays) in the captured image.</summary>
    public const uint CAPTUREBLT = 0x40000000;
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;
    /// <summary>Windows 8.1+: render DirectComposition/DirectX content too (Chromium, WinUI, WPF hardware rendering).</summary>
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const int ERROR_ACCESS_DENIED = 5;

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [LibraryImport(User32)]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport(User32)]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateDIBSection(nint hdc, in BITMAPINFO info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport(Gdi32)]
    public static partial nint SelectObject(nint hdc, nint gdiObject);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint gdiObject);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdcDest, int x, int y, int width, int height, nint hdcSource, int xSource, int ySource, uint rop);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport(User32)]
    public static partial nint SetThreadDpiAwarenessContext(nint context);

    // callback: delegate* unmanaged<nint hMonitor, nint hdc, RECT* rect, nint lParam, int /*BOOL*/>
    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, nint callback, nint lParam);

    [LibraryImport(User32)]
    public static partial int GetSystemMetrics(int index);
}

/// <summary>Temporarily switches the calling thread to per-monitor-v2 DPI awareness (physical coordinates).</summary>
internal readonly struct DpiScope : IDisposable
{
    private static volatile bool s_unavailable;
    private readonly nint _previous;

    private DpiScope(nint previous) => _previous = previous;

    public static DpiScope Enter()
    {
        if (s_unavailable) return default;
        try
        {
            nint previous = CaptureNativeMethods.SetThreadDpiAwarenessContext(CaptureNativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previous == 0) previous = CaptureNativeMethods.SetThreadDpiAwarenessContext(CaptureNativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
            return new DpiScope(previous);
        }
        catch (EntryPointNotFoundException)
        {
            s_unavailable = true;
            return default;
        }
    }

    public void Dispose()
    {
        if (_previous != 0) CaptureNativeMethods.SetThreadDpiAwarenessContext(_previous);
    }
}
