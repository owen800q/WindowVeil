using System.Runtime.InteropServices;
using WindowVeil.Capture.Interop;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Platform;
using static WindowVeil.Capture.Interop.CaptureNativeMethods;

namespace WindowVeil.Capture;

/// <summary>Synchronous GDI capture primitives. Every GDI object is released in finally blocks.</summary>
internal static class GdiCapture
{
    /// <summary>Union of all monitors in physical pixels.</summary>
    public static unsafe PixelRect GetVirtualScreenBounds()
    {
        using var scope = DpiScope.Enter();
        var union = PixelRect.Empty;
        delegate* unmanaged<nint, nint, RECT*, nint, int> callback = &MonitorCallback;
        EnumDisplayMonitors(0, 0, (nint)callback, (nint)(&union));
        if (!union.IsEmpty) return union;
        return new PixelRect(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    [UnmanagedCallersOnly]
    private static unsafe int MonitorCallback(nint monitor, nint hdc, RECT* rect, nint lParam)
    {
        var union = (PixelRect*)lParam;
        *union = union->Union(rect->ToPixelRect());
        return 1;
    }

    /// <summary>
    /// BitBlt of the composed desktop with CAPTUREBLT, so layered windows (WindowVeil overlays included) are part of the
    /// image. Coordinates are virtual-desktop physical pixels (may be negative). The region is clipped to the virtual screen.
    /// </summary>
    public static CaptureResult CaptureDesktop(PixelRect region)
    {
        if (region.IsEmpty) return CaptureResult.Failure(CaptureError.InvalidRegion, $"The capture region {region} is empty.");
        using var scope = DpiScope.Enter(); // GetDC(NULL)/BitBlt use physical coordinates only in a per-monitor context
        var screen = GetVirtualScreenBounds();
        var clipped = region.Intersect(screen);
        if (clipped.IsEmpty)
            return CaptureResult.Failure(CaptureError.InvalidRegion, $"The capture region {region} is outside the virtual screen {screen}.");

        nint screenDc = GetDC(0);
        if (screenDc == 0) return CaptureResult.Failure(CaptureError.AccessDenied, "GetDC(NULL) failed (secure desktop or no interactive session?).");
        nint memDc = 0, bitmap = 0, previous = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == 0) return Win32Failure(CaptureError.Failed, "CreateCompatibleDC");
            bitmap = CreateTopDownDib(screenDc, clipped.Width, clipped.Height, out nint bits);
            if (bitmap == 0) return Win32Failure(CaptureError.Failed, "CreateDIBSection");
            previous = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, clipped.Width, clipped.Height, screenDc, clipped.X, clipped.Y, SRCCOPY | CAPTUREBLT))
            {
                int error = Marshal.GetLastPInvokeError();
                return CaptureResult.Failure(error == ERROR_ACCESS_DENIED ? CaptureError.AccessDenied : CaptureError.Failed,
                    $"BitBlt failed (error {error}); the desktop may be locked or a secure desktop is active.");
            }
            GdiFlush();
            var image = CopyPixels(bits, clipped.Width, new PixelRect(0, 0, clipped.Width, clipped.Height));
            return CaptureResult.Success(new CapturedFrame(image, clipped, CaptureSource.Desktop));
        }
        finally
        {
            if (previous != 0) SelectObject(memDc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memDc != 0) DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    /// <summary>
    /// PrintWindow(PW_RENDERFULLCONTENT) of one window, cropped to <paramref name="region"/>. The window renders itself
    /// into our bitmap, so independently hosted overlay windows are never part of the result (OCR cannot see masks).
    /// PrintWindow renders the full <c>GetWindowRect</c> rectangle, so screen coordinates are mapped with it rather than
    /// with the DWM frame bounds.
    /// </summary>
    public static CaptureResult CaptureWindow(TargetWindowInfo? window, PixelRect region)
    {
        if (window is null) return CaptureResult.Failure(CaptureError.WindowUnavailable, "CaptureSource.Window requires CaptureRequest.Window.");
        if (region.IsEmpty) return CaptureResult.Failure(CaptureError.InvalidRegion, $"The capture region {region} is empty.");
        nint hwnd = window.Handle;
        if (hwnd == 0 || !IsWindow(hwnd)) return CaptureResult.Failure(CaptureError.WindowUnavailable, $"Window 0x{hwnd:X} no longer exists.");
        if (IsIconic(hwnd)) return CaptureResult.Failure(CaptureError.WindowUnavailable, $"Window 0x{hwnd:X} is minimized.");

        using var scope = DpiScope.Enter();
        if (!GetWindowRect(hwnd, out var wr)) return CaptureResult.Failure(CaptureError.WindowUnavailable, $"GetWindowRect(0x{hwnd:X}) failed.");
        var windowRect = wr.ToPixelRect();
        if (windowRect.IsEmpty) return CaptureResult.Failure(CaptureError.WindowUnavailable, $"Window 0x{hwnd:X} has no area.");
        var crop = region.Intersect(windowRect);
        if (crop.IsEmpty)
            return CaptureResult.Failure(CaptureError.InvalidRegion, $"The capture region {region} does not intersect window 0x{hwnd:X} at {windowRect}.");

        nint screenDc = GetDC(0);
        if (screenDc == 0) return CaptureResult.Failure(CaptureError.AccessDenied, "GetDC(NULL) failed.");
        nint memDc = 0, bitmap = 0, previous = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == 0) return Win32Failure(CaptureError.Failed, "CreateCompatibleDC");
            bitmap = CreateTopDownDib(screenDc, windowRect.Width, windowRect.Height, out nint bits);
            if (bitmap == 0) return Win32Failure(CaptureError.Failed, "CreateDIBSection");
            previous = SelectObject(memDc, bitmap);
            if (!PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT))
            {
                if (!IsWindow(hwnd)) return CaptureResult.Failure(CaptureError.WindowUnavailable, $"Window 0x{hwnd:X} was destroyed during capture.");
                return CaptureResult.Failure(CaptureError.Failed, $"PrintWindow(0x{hwnd:X}) failed (error {Marshal.GetLastPInvokeError()}).");
            }
            GdiFlush();
            // Judge the whole window, not the crop: a uniform crop is legitimate (a blank area), a uniform window is not.
            if (IsUniform(bits, windowRect.Width, windowRect.Height))
            {
                return CaptureResult.Failure(CaptureError.EmptyContent,
                    $"PrintWindow returned a uniform image for window 0x{hwnd:X} (protected/DRM content, display affinity, or a window that cannot render off-screen).");
            }
            var local = CoordinateConverter.ScreenToRelative(crop, windowRect.Location);
            var image = CopyPixels(bits, windowRect.Width, local);
            return CaptureResult.Success(new CapturedFrame(image, crop, CaptureSource.Window));
        }
        finally
        {
            if (previous != 0) SelectObject(memDc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memDc != 0) DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    private static CaptureResult Win32Failure(CaptureError error, string api) =>
        CaptureResult.Failure(error, $"{api} failed (error {Marshal.GetLastPInvokeError()}).");

    /// <summary>32 bpp top-down (negative height) BI_RGB DIB section; rows are width*4 bytes (always DWORD aligned).</summary>
    private static nint CreateTopDownDib(nint hdc, int width, int height, out nint bits)
    {
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            },
        };
        nint bitmap = CreateDIBSection(hdc, in info, DIB_RGB_COLORS, out bits, 0, 0);
        if (bitmap != 0 && bits == 0)
        {
            DeleteObject(bitmap);
            return 0;
        }
        return bitmap;
    }

    /// <summary>Copies a rectangle out of a 32bpp top-down DIB into a new BgraImage, forcing alpha to 255.</summary>
    private static unsafe BgraImage CopyPixels(nint bits, int sourceWidth, PixelRect rect)
    {
        var image = new BgraImage(rect.Width, rect.Height);
        int sourceStride = sourceWidth * 4;
        fixed (byte* destination = image.Pixels)
        {
            for (int y = 0; y < rect.Height; y++)
            {
                uint* src = (uint*)((byte*)bits + (long)(rect.Y + y) * sourceStride + rect.X * 4L);
                uint* dst = (uint*)(destination + (long)y * image.Stride);
                // GDI leaves the alpha byte undefined (usually 0) for BI_RGB.
                for (int x = 0; x < rect.Width; x++) dst[x] = src[x] | 0xFF000000u;
            }
        }
        return image;
    }

    private static unsafe bool IsUniform(nint bits, int width, int height)
    {
        uint* p = (uint*)bits;
        long count = (long)width * height;
        uint first = p[0] & 0x00FFFFFFu;
        for (long i = 1; i < count; i++)
            if ((p[i] & 0x00FFFFFFu) != first) return false;
        return true;
    }
}
