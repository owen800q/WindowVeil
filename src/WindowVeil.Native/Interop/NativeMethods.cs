using System.Runtime.InteropServices;

namespace WindowVeil.Native.Interop;

/// <summary>RECT (left/top/right/bottom, right and bottom exclusive).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public RECT(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }

    public readonly Geometry.PixelRect ToPixelRect() => Geometry.PixelRect.FromLTRB(Left, Top, Right, Bottom);

    public static RECT From(Geometry.PixelRect r) => new(r.X, r.Y, r.Right, r.Bottom);
}

/// <summary>POINT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y) { X = x; Y = y; }
}

/// <summary>MSG. There is no trailing lPrivate field on Windows (it only exists under _MAC).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
}

/// <summary>WNDCLASSEXW. String fields are raw pointers so the struct stays blittable.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public nint lpszMenuName;
    public nint lpszClassName;
    public nint hIconSm;
}

/// <summary>PAINTSTRUCT.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PAINTSTRUCT
{
    public nint hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public fixed byte rgbReserved[32];
}

/// <summary>MONITORINFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

/// <summary>MINMAXINFO.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

/// <summary>
/// Win32 declarations used by WindowVeil.Native. Every signature follows the documented W (UTF-16) entry point;
/// BOOL is marshaled as a 4-byte <see cref="UnmanagedType.Bool"/>, handles/WPARAM/LPARAM are <see cref="nint"/>.
/// Callbacks are passed as raw function pointers to <c>[UnmanagedCallersOnly]</c> static methods, which can never be
/// collected by the GC (unlike marshaled delegates).
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string DwmApi = "dwmapi.dll";

    // ---- window messages
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_NCDESTROY = 0x0082;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_USER = 0x0400;
    public const uint WM_APP = 0x8000;

    public const int HTTRANSPARENT = -1;
    public const int MA_ACTIVATE = 1;
    public const int MA_NOACTIVATE = 3;

    // ---- styles
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint LWA_COLORKEY = 0x1;
    public const uint LWA_ALPHA = 0x2;

    // ---- SetWindowPos
    public static readonly nint HWND_TOP = 0;
    public static readonly nint HWND_BOTTOM = 1;
    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public static readonly nint HWND_MESSAGE = -3;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    // ---- GetWindow
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;

    // ---- monitors / metrics
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // ---- DPI awareness contexts (pseudo handles)
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // ---- DWM
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint DWMWA_CLOAKED = 14;

    // ---- regions
    public const int RGN_AND = 1;
    public const int RGN_OR = 2;
    public const int RGN_DIFF = 4;
    public const int ERROR = 0;

    // ---- display affinity
    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    // ---- processes
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;
    public const uint STILL_ACTIVE = 259;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    public const int ERROR_CLASS_HAS_WINDOWS = 1412;

    public const uint GR_GDIOBJECTS = 0;
    public const uint GR_USEROBJECTS = 1;

    public const uint PM_NOREMOVE = 0x0000;

    public static readonly nint IDC_ARROW = 32512;

    // ------------------------------------------------------------------ user32: classes & windows

    [LibraryImport(User32, EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WNDCLASSEXW wndClass);

    [LibraryImport(User32, EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string className, nint hInstance);

    [LibraryImport(User32, EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport(User32, EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32, EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint cursorName);

    // ------------------------------------------------------------------ user32: messages

    /// <summary>Returns -1 on error, 0 for WM_QUIT, otherwise non-zero (hence int, not bool).</summary>
    [LibraryImport(User32, EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out MSG msg, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport(User32, EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out MSG msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG msg);

    [LibraryImport(User32, EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in MSG msg);

    [LibraryImport(User32, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32, EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32)]
    public static partial void PostQuitMessage(int exitCode);

    // ------------------------------------------------------------------ user32: DPI

    [LibraryImport(User32)]
    public static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport(User32)]
    public static partial uint GetDpiForWindow(nint hwnd);

    // ------------------------------------------------------------------ user32: enumeration & queries

    // callback: delegate* unmanaged<nint hwnd, nint lParam, int /*BOOL*/>
    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(nint callback, nint lParam);

    [LibraryImport(User32, EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static partial int GetWindowText(nint hwnd, char* buffer, int maxCount);

    [LibraryImport(User32, EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    public static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport(User32, EntryPoint = "GetClassNameW", SetLastError = true)]
    public static partial int GetClassName(nint hwnd, char* buffer, int maxCount);

    [LibraryImport(User32, SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, ref POINT point);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport(User32)]
    public static partial nint GetForegroundWindow();

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint GetWindow(nint hwnd, uint cmd);

    [LibraryImport(User32)]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport(User32)]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport(User32, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    // callback: delegate* unmanaged<nint hMonitor, nint hdc, RECT* rect, nint lParam, int /*BOOL*/>
    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, nint callback, nint lParam);

    [LibraryImport(User32)]
    public static partial int GetSystemMetrics(int index);

    // GetWindowLongPtrW/SetWindowLongPtrW are only exported by 64-bit user32; on 32-bit they are macros for the
    // non-Ptr functions. The wrappers below pick the right export at runtime.
    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr64(nint hwnd, int index);

    [LibraryImport(User32, EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(nint hwnd, int index);

    [LibraryImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [LibraryImport(User32, EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong32(nint hwnd, int index, int value);

    public static nint GetWindowLongPtr(nint hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    public static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLong32(hwnd, index, (int)value);

    // ------------------------------------------------------------------ user32: positioning & appearance

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint BeginDeferWindowPos(int numWindows);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint DeferWindowPos(nint winPosInfo, nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndDeferWindowPos(nint winPosInfo);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLayeredWindowAttributes(nint hwnd, out uint colorKey, out byte alpha, out uint flags);

    /// <summary>On success the system owns <paramref name="region"/>; the caller must not use or delete it.</summary>
    [LibraryImport(User32, SetLastError = true)]
    public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport(User32)]
    public static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, in PAINTSTRUCT paint);

    [LibraryImport(User32)]
    public static partial int FillRect(nint hdc, in RECT rect, nint brush);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, RECT* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport(User32)]
    public static partial uint GetGuiResources(nint process, uint flags);

    // ------------------------------------------------------------------ gdi32

    [LibraryImport(Gdi32)]
    public static partial nint CreateSolidBrush(uint colorRef);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint gdiObject);

    [LibraryImport(Gdi32)]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport(Gdi32)]
    public static partial int CombineRgn(nint destination, nint source1, nint source2, int mode);

    // ------------------------------------------------------------------ kernel32

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport(Kernel32)]
    public static partial uint GetCurrentThreadId();

    [LibraryImport(Kernel32)]
    public static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    // ------------------------------------------------------------------ dwmapi

    [LibraryImport(DwmApi)]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport(DwmApi)]
    public static partial int DwmFlush();

    // ------------------------------------------------------------------ helpers

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    public static uint ToColorRef(Imaging.OverlayColor c) => c.R | ((uint)c.G << 8) | ((uint)c.B << 16);

    public static string GetWindowTextString(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        len = Math.Min(len, 4096);
        char* buffer = stackalloc char[len + 1];
        int copied = GetWindowText(hwnd, buffer, len + 1);
        return copied <= 0 ? "" : new string(buffer, 0, copied);
    }

    public static string GetClassNameString(nint hwnd)
    {
        // Class names are limited to 256 characters.
        char* buffer = stackalloc char[257];
        int copied = GetClassName(hwnd, buffer, 257);
        return copied <= 0 ? "" : new string(buffer, 0, copied);
    }
}
