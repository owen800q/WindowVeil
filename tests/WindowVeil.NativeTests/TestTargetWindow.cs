using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Native;
using WindowVeil.Native.Interop;
using static WindowVeil.Native.Interop.NativeMethods;

namespace WindowVeil.NativeTests;

/// <summary>
/// A plain Win32 top-level window owned by its own <see cref="NativeMessageThread"/>, used as an overlay target so tests
/// do not depend on external applications. The client area is filled with <see cref="Color"/>, except for a 16x16 white
/// square in the top-left corner (so a PrintWindow capture is never uniform).
/// </summary>
internal sealed class TestTargetWindow : IDisposable
{
    public const string ClassName = "WindowVeilTests.Target";
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private static readonly ConcurrentDictionary<nint, TestTargetWindow> Map = new();

    private readonly nint _brush;
    private readonly nint _markerBrush;
    private bool _classAcquired;

    private unsafe TestTargetWindow(PixelRect bounds, OverlayColor color, string title)
    {
        Color = color;
        Title = title;
        Thread = new NativeMessageThread("test target window");
        _brush = CreateSolidBrush(ToColorRef(color));
        _markerBrush = CreateSolidBrush(ToColorRef(OverlayColor.White));
        try
        {
            Handle = Thread.Invoke(() =>
            {
                delegate* unmanaged<nint, uint, nint, nint, nint> proc = &WndProc;
                WindowClassRegistry.Acquire(ClassName, (nint)proc, LoadCursor(0, IDC_ARROW));
                _classAcquired = true;
                nint hwnd = CreateWindowEx(0, ClassName, title, WS_OVERLAPPEDWINDOW, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    0, 0, WindowClassRegistry.ModuleHandle, 0);
                if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                Map[hwnd] = this;
                // Show without activation and raise to the top of the non-topmost band so the desktop shows it.
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOACTIVATE);
                return hwnd;
            });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public NativeMessageThread Thread { get; }

    public nint Handle { get; }

    public string Title { get; }

    public OverlayColor Color { get; }

    public static TestTargetWindow Create(PixelRect? bounds = null, OverlayColor? color = null, string? title = null) =>
        new(bounds ?? new PixelRect(200, 160, 480, 360), color ?? OverlayColor.FromRgb(0x20, 0x80, 0xC0),
            title ?? "WindowVeil Test Target " + Guid.NewGuid().ToString("n")[..8]);

    public bool IsAlive => NativeMethods.IsWindow(Handle);

    /// <summary>Moves/resizes the window on its own thread (outer window rectangle, physical pixels).</summary>
    public void MoveTo(PixelRect bounds) =>
        Thread.Invoke(() => SetWindowPos(Handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE));

    /// <summary>Raises the window to the top of the non-topmost band without activating it.</summary>
    public void BringToTop() =>
        Thread.Invoke(() => SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE));

    public void Destroy()
    {
        if (Thread.IsDisposed) return;
        Thread.Invoke(() =>
        {
            if (NativeMethods.IsWindow(Handle)) DestroyWindow(Handle);
        });
    }

    public void Dispose()
    {
        try
        {
            if (!Thread.IsDisposed)
            {
                Thread.Invoke(() =>
                {
                    if (Handle != 0 && NativeMethods.IsWindow(Handle)) DestroyWindow(Handle);
                    if (_classAcquired) WindowClassRegistry.Release(ClassName);
                    _classAcquired = false;
                });
            }
        }
        finally
        {
            Map.TryRemove(Handle, out _);
            Thread.Dispose();
            DeleteObject(_brush);
            DeleteObject(_markerBrush);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_ERASEBKGND) return 1;
            if (msg == WM_PAINT && Map.TryGetValue(hwnd, out var self))
            {
                nint hdc = BeginPaint(hwnd, out PAINTSTRUCT ps);
                if (hdc != 0)
                {
                    GetClientRect(hwnd, out RECT client);
                    FillRect(hdc, in client, self._brush);
                    FillRect(hdc, new RECT(0, 0, 16, 16), self._markerBrush);
                    EndPaint(hwnd, in ps);
                }
                return 0;
            }
        }
        catch
        {
            // never unwind into user32
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
