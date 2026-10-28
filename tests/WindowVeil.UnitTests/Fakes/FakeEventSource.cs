using WindowVeil.Platform;

namespace WindowVeil.UnitTests.Fakes;

/// <summary>Window events raised manually by tests. Tracks live subscriptions.</summary>
internal sealed class FakeEventSource : IWindowEventSource, IDisposable
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public bool Disposed { get; private set; }

    public int TotalSubscriptions;

    public IReadOnlyList<nint> SubscribedHandles { get { lock (_gate) return _subscriptions.Select(s => s.Handle).ToArray(); } }

    public int ActiveCount { get { lock (_gate) return _subscriptions.Count; } }

    public IDisposable Subscribe(TargetWindowInfo window, Action<WindowEvent> handler)
    {
        var s = new Subscription(this, window.Handle, handler);
        lock (_gate) _subscriptions.Add(s);
        Interlocked.Increment(ref TotalSubscriptions);
        return s;
    }

    /// <summary>Raises an event for one window. Foreground and z-order events go to every subscriber, like global WinEvents.</summary>
    public void Raise(nint handle, WindowEventKind kind)
    {
        Subscription[] targets;
        lock (_gate)
        {
            targets = kind is WindowEventKind.ForegroundChanged or WindowEventKind.ZOrderChanged
                ? _subscriptions.ToArray()
                : _subscriptions.Where(s => s.Handle == handle).ToArray();
        }
        var e = new WindowEvent(kind, handle, DateTime.UtcNow.Ticks);
        foreach (var s in targets) s.Handler(e);
    }

    public void Dispose() => Disposed = true;

    private sealed class Subscription(FakeEventSource owner, nint handle, Action<WindowEvent> handler) : IDisposable
    {
        public nint Handle { get; } = handle;
        public Action<WindowEvent> Handler { get; } = handler;

        public void Dispose()
        {
            lock (owner._gate) owner._subscriptions.Remove(this);
        }
    }
}
