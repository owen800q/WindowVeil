using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WindowVeil.Geometry;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using WindowVeil.Wpf;
using DpiScale = WindowVeil.Geometry.DpiScale;

namespace WindowVeil.WpfTests.Infrastructure;

/// <summary>
/// An in-process stand-in for an external application window: a plain WPF window on its own STA dispatcher thread,
/// unrelated to the renderer's thread (as a real target would be).
/// </summary>
internal sealed class TestTargetWindow : IAsyncDisposable
{
    private readonly WpfDispatcherHost _host;
    private readonly Window _window;

    private TestTargetWindow(WpfDispatcherHost host, Window window, nint handle)
    {
        _host = host;
        _window = window;
        Handle = handle;
    }

    public static readonly PixelRect DefaultBounds = new(200, 200, 600, 400);

    public nint Handle { get; }

    public string Title { get; private init; } = "";

    public static async Task<TestTargetWindow> CreateAsync(PixelRect? bounds = null)
    {
        var b = bounds ?? DefaultBounds;
        var title = "WindowVeil test target " + Guid.NewGuid().ToString("n")[..8];
        var host = new WpfDispatcherHost("Test target window");
        try
        {
            var (window, handle) = await host.InvokeAsync(() =>
            {
                var w = new Window
                {
                    Title = title,
                    WindowStyle = WindowStyle.SingleBorderWindow,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Background = Brushes.LightGray,
                    ShowActivated = true,
                };
                var h = new WindowInteropHelper(w).EnsureHandle();
                // Physical pixels, like a real external window measured by the tracker.
                TestNative.SetWindowPos(h, 0, b.X, b.Y, b.Width, b.Height, TestNative.SWP_NOZORDER | TestNative.SWP_NOACTIVATE);
                w.Show();
                w.Activate();
                return (w, h);
            });
            var target = new TestTargetWindow(host, window, handle) { Title = title };
            await target.BringToForegroundAsync();
            return target;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    /// <summary>Best effort: the foreground lock may refuse when the test process is not in the foreground.</summary>
    public async Task BringToForegroundAsync()
    {
        await _host.InvokeAsync(() => { _window.Activate(); });
        TestNative.SetForegroundWindow(Handle);
        await Task.Delay(50);
    }

    public PixelRect Bounds => TestNative.GetWindowBounds(Handle);

    public PixelRect ClientBounds => TestNative.GetClientBounds(Handle);

    public DpiScale Dpi => TestNative.GetDpi(Handle);

    /// <summary>A visible frame targeting this window.</summary>
    public OverlayFrame Frame(PixelRect bounds) => new()
    {
        Visible = true,
        Bounds = bounds,
        Regions = [bounds],
        TargetRects = [bounds],
        TargetWindow = Handle,
        TargetWindowBounds = Bounds,
        MonitorBounds = TestNative.GetMonitorBounds(Handle),
        Dpi = Dpi,
        TargetIsForeground = TestNative.GetForegroundWindow() == Handle,
    };

    /// <summary>A rectangle inside the target window, offset from its top-left corner.</summary>
    public PixelRect Inside(int dx, int dy, int width, int height)
    {
        var b = Bounds;
        return new PixelRect(b.X + dx, b.Y + dy, width, height);
    }

    public TargetWindowInfo Snapshot() => new()
    {
        Handle = Handle,
        ProcessId = Environment.ProcessId,
        ProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName,
        Title = Title,
        ClassName = "",
        Bounds = Bounds,
        ClientBounds = ClientBounds,
        IsVisible = TestNative.IsWindowVisible(Handle),
        IsMinimized = TestNative.IsIconic(Handle),
        IsForeground = TestNative.GetForegroundWindow() == Handle,
        Dpi = Dpi,
        MonitorBounds = TestNative.GetMonitorBounds(Handle),
    };

    public async ValueTask DisposeAsync()
    {
        try { await _host.InvokeAsync(() => _window.Close()); }
        catch (WindowVeilException) { }
        await _host.DisposeAsync();
    }
}
