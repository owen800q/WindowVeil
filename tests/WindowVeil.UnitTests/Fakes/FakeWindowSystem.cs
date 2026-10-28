using WindowVeil.Geometry;
using WindowVeil.Platform;

namespace WindowVeil.UnitTests.Fakes;

/// <summary>
/// In-memory desktop. Windows are mutable; "destroying" removes the window and kills its process, "relaunching"
/// creates an equivalent window with a new handle and process id (like an application restart).
/// </summary>
internal sealed class FakeWindowSystem : IWindowSystem, IDisposable
{
    /// <summary>Client area inset from the outer bounds (left, top/caption, right, bottom).</summary>
    public static readonly (int L, int T, int R, int B) Frame = (8, 31, 8, 8);

    private readonly object _gate = new();
    private readonly Dictionary<nint, TargetWindowInfo> _windows = [];
    private readonly HashSet<int> _alive = [];
    private nint _nextHandle = 0x10000;
    private int _nextPid = 1000;

    public bool Disposed { get; private set; }

    public int EnumerateCalls;

    public PixelRect VirtualScreenBounds => new(-1920, 0, 3840, 1080);

    public TargetWindowInfo Add(string processName = "RetroCap", string title = "RetroCap", PixelRect? bounds = null,
        double dpiPercent = 100, bool foreground = true, string className = "RetroCapMain")
    {
        lock (_gate)
        {
            var b = bounds ?? new PixelRect(100, 100, 800, 600);
            var w = new TargetWindowInfo
            {
                Handle = _nextHandle += 0x10,
                ProcessId = ++_nextPid,
                ProcessName = processName,
                Title = title,
                ClassName = className,
                Bounds = b,
                ClientBounds = ClientOf(b),
                Dpi = DpiScale.FromPercent(dpiPercent),
                IsForeground = foreground,
                MonitorBounds = new PixelRect(0, 0, 1920, 1080),
            };
            _windows[w.Handle] = w;
            _alive.Add(w.ProcessId);
            return w;
        }
    }

    public static PixelRect ClientOf(PixelRect b) => PixelRect.FromLTRB(b.X + Frame.L, b.Y + Frame.T, b.Right - Frame.R, b.Bottom - Frame.B);

    public TargetWindowInfo Get(nint handle) { lock (_gate) return _windows[handle]; }

    public TargetWindowInfo Update(nint handle, Func<TargetWindowInfo, TargetWindowInfo> change)
    {
        lock (_gate) return _windows[handle] = change(_windows[handle]);
    }

    public TargetWindowInfo Move(nint handle, int dx, int dy) => Update(handle, w => w with
    {
        Bounds = w.Bounds.Offset(dx, dy),
        ClientBounds = w.ClientBounds.Offset(dx, dy),
    });

    public TargetWindowInfo Resize(nint handle, int width, int height) => Update(handle, w =>
    {
        var b = w.Bounds with { Width = width, Height = height };
        return w with { Bounds = b, ClientBounds = ClientOf(b) };
    });

    public TargetWindowInfo SetMinimized(nint handle, bool minimized) => Update(handle, w => w with { IsMinimized = minimized });

    public TargetWindowInfo SetForeground(nint handle, bool foreground) => Update(handle, w => w with { IsForeground = foreground });

    /// <summary>Removes the window and ends its process.</summary>
    public void Destroy(nint handle)
    {
        lock (_gate)
        {
            if (_windows.Remove(handle, out var w)) _alive.Remove(w.ProcessId);
        }
    }

    /// <summary>Ends the process but leaves the (zombie) window snapshot in place.</summary>
    public void KillProcess(int processId)
    {
        lock (_gate) _alive.Remove(processId);
    }

    /// <summary>Starts a new instance of the same application (new handle, new process id).</summary>
    public TargetWindowInfo Relaunch(TargetWindowInfo old, PixelRect? bounds = null) =>
        Add(old.ProcessName, old.Title, bounds ?? old.Bounds, old.Dpi.Percent, old.IsForeground, old.ClassName);

    public IReadOnlyList<TargetWindowInfo> EnumerateWindows()
    {
        Interlocked.Increment(ref EnumerateCalls);
        lock (_gate) return _windows.Values.ToArray();
    }

    public TargetWindowInfo? GetWindow(nint handle)
    {
        lock (_gate) return _windows.TryGetValue(handle, out var w) ? w : null;
    }

    public bool IsProcessAlive(int processId)
    {
        lock (_gate) return _alive.Contains(processId);
    }

    public void Dispose() => Disposed = true;
}
