using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowVeil.Geometry;
using WindowVeil.Native.Interop;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using static WindowVeil.Native.Interop.NativeMethods;

namespace WindowVeil.Native;

/// <summary>
/// <see cref="IWindowSystem"/> backed by USER32/DWM. Thread-safe; every query temporarily switches the calling thread to
/// per-monitor-v2 DPI awareness, so all returned rectangles are physical virtual-desktop pixels regardless of the
/// host process' DPI awareness.
/// </summary>
public sealed class Win32WindowSystem : IWindowSystem
{
    /// <summary>Window classes of WindowVeil's own windows, never reported by <see cref="EnumerateWindows"/>.</summary>
    public static IReadOnlySet<string> OwnWindowClasses { get; } =
        new HashSet<string>(StringComparer.Ordinal) { NativeOverlayRenderer.WindowClassName, "WindowVeil.MessageWindow" };

    private static readonly long ProcessNameTtl = Stopwatch.Frequency * 30;
    private readonly ConcurrentDictionary<int, (string Name, long Expires)> _processNames = new();

    /// <inheritdoc />
    public unsafe IReadOnlyList<TargetWindowInfo> EnumerateWindows()
    {
        var handles = new List<nint>(256);
        var gc = GCHandle.Alloc(handles);
        try
        {
            delegate* unmanaged<nint, nint, int> callback = &EnumWindowsCallback;
            EnumWindows((nint)callback, GCHandle.ToIntPtr(gc));
        }
        finally { gc.Free(); }

        var result = new List<TargetWindowInfo>(handles.Count);
        using var scope = DpiAwarenessScope.EnterPerMonitorV2();
        var foreground = GetForegroundWindow();
        foreach (var hwnd in handles)
        {
            if (OverlayWindowRegistry.IsOverlay(hwnd)) continue;
            var className = GetClassNameString(hwnd);
            if (OwnWindowClasses.Contains(className)) continue;
            var info = Snapshot(hwnd, className, foreground);
            if (info is not null) result.Add(info);
        }
        return result;
    }

    /// <inheritdoc />
    public TargetWindowInfo? GetWindow(nint handle)
    {
        if (handle == 0 || !IsWindow(handle)) return null;
        using var scope = DpiAwarenessScope.EnterPerMonitorV2();
        return Snapshot(handle, GetClassNameString(handle), GetForegroundWindow());
    }

    /// <inheritdoc />
    public bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        if (processId == Environment.ProcessId) return true;
        nint process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process != 0)
        {
            try { return WaitForSingleObject(process, 0) == WAIT_TIMEOUT; }
            finally { CloseHandle(process); }
        }
        int error = Marshal.GetLastPInvokeError();
        if (error == ERROR_ACCESS_DENIED)
        {
            // SYNCHRONIZE can be denied (for example for protected processes); query the exit code instead.
            process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (process == 0)
            {
                // Access denied means the process object exists; an exited process yields ERROR_INVALID_PARAMETER.
                return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED;
            }
            try { return GetExitCodeProcess(process, out uint code) && code == STILL_ACTIVE; }
            finally { CloseHandle(process); }
        }
        return false; // ERROR_INVALID_PARAMETER: no such process.
    }

    /// <inheritdoc />
    public PixelRect VirtualScreenBounds
    {
        get
        {
            using var scope = DpiAwarenessScope.EnterPerMonitorV2();
            return GetVirtualScreenBoundsCore();
        }
    }

    /// <summary>Union of all monitor rectangles (physical). Must be called in a per-monitor-aware context.</summary>
    internal static unsafe PixelRect GetVirtualScreenBoundsCore()
    {
        // EnumDisplayMonitors in a PMv2 context reports physical monitor rectangles; GetSystemMetrics is documented as
        // not DPI aware, so it is only used as a fallback.
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

    [UnmanagedCallersOnly]
    private static int EnumWindowsCallback(nint hwnd, nint lParam)
    {
        try
        {
            ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
            return 1;
        }
        catch
        {
            return 0; // stop enumeration; never unwind into user32
        }
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>Builds a snapshot. Must be called in a per-monitor-aware context.</summary>
    private TargetWindowInfo? Snapshot(nint hwnd, string className, nint foreground)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null; // the window was destroyed meanwhile

        var bounds = GetVisibleBounds(hwnd);
        var client = PixelRect.Empty;
        if (GetClientRect(hwnd, out var rc))
        {
            var origin = new POINT(0, 0);
            if (ClientToScreen(hwnd, ref origin)) client = new PixelRect(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        }

        var monitorBounds = PixelRect.Empty;
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != 0)
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref mi)) monitorBounds = mi.rcMonitor.ToPixelRect();
        }

        var info = new TargetWindowInfo
        {
            Handle = hwnd,
            ProcessId = (int)pid,
            ProcessName = GetProcessName((int)pid),
            Title = GetWindowTextString(hwnd),
            ClassName = className,
            Bounds = bounds,
            ClientBounds = client,
            IsVisible = IsWindowVisible(hwnd) && !IsCloaked(hwnd),
            IsMinimized = IsIconic(hwnd),
            IsForeground = foreground == hwnd,
            Dpi = new DpiScale(GetWindowDpi(hwnd)),
            MonitorBounds = monitorBounds,
        };
        return IsWindow(hwnd) ? info : null;
    }

    /// <summary>
    /// DWM extended frame bounds (excludes the invisible resize borders; always physical pixels), falling back to
    /// GetWindowRect (physical in a PMv2 context).
    /// </summary>
    internal static unsafe PixelRect GetVisibleBounds(nint hwnd)
    {
        RECT r;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)) >= 0 && r.Right > r.Left && r.Bottom > r.Top)
            return r.ToPixelRect();
        return GetWindowRect(hwnd, out r) ? r.ToPixelRect() : PixelRect.Empty;
    }

    internal static unsafe bool IsCloaked(nint hwnd)
    {
        int cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) >= 0 && cloaked != 0;
    }

    private static volatile bool s_noGetDpiForWindow;

    internal static uint GetWindowDpi(nint hwnd)
    {
        if (s_noGetDpiForWindow) return 96;
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96 : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            s_noGetDpiForWindow = true; // Windows older than 10 1607
            return 96;
        }
    }

    private string GetProcessName(int pid)
    {
        long now = Stopwatch.GetTimestamp();
        if (_processNames.TryGetValue(pid, out var cached) && cached.Expires > now) return cached.Name;
        string name = "";
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Exited, access denied or otherwise unavailable.
        }
        // Process ids are reused, so names expire.
        _processNames[pid] = (name, now + ProcessNameTtl);
        if (_processNames.Count > 4096) _processNames.Clear();
        return name;
    }
}
