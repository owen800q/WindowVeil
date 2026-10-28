using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Platform;

namespace WindowVeil.Tracking;

/// <summary>
/// <see cref="IWindowEventSource"/> that polls <see cref="IWindowSystem.GetWindow"/> and reports differences between
/// consecutive snapshots. Use it where WinEvent hooks are unavailable (for example across desktop or integrity
/// boundaries) or in tests with a fake window system. It cannot detect <see cref="WindowEventKind.ContentChanged"/>
/// or z-order changes.
/// </summary>
/// <remarks>Events are raised on a timer (thread pool) thread. Each subscription polls independently and never overlaps itself.</remarks>
public sealed class PollingWindowEventSource : IWindowEventSource, IDisposable
{
    private readonly IWindowSystem _windowSystem;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HashSet<Subscription> _subscriptions = [];
    private bool _disposed;

    /// <summary>Creates a polling source.</summary>
    /// <param name="windowSystem">Window system to poll.</param>
    /// <param name="interval">Poll interval (default 100 ms, minimum 10 ms).</param>
    /// <param name="timeProvider">Clock and timers (overridable in tests).</param>
    /// <param name="logger">Logger.</param>
    public PollingWindowEventSource(IWindowSystem windowSystem, TimeSpan? interval = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        _windowSystem = windowSystem ?? throw new ArgumentNullException(nameof(windowSystem));
        var i = interval ?? TimeSpan.FromMilliseconds(100);
        Interval = i < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : i;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Poll interval.</summary>
    public TimeSpan Interval { get; }

    /// <inheritdoc />
    public IDisposable Subscribe(TargetWindowInfo window, Action<WindowEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(handler);
        var s = new Subscription(this, window, handler);
        lock (_subscriptions)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(s);
        }
        s.Start();
        return s;
    }

    /// <summary>Stops every subscription.</summary>
    public void Dispose()
    {
        Subscription[] all;
        lock (_subscriptions)
        {
            if (_disposed) return;
            _disposed = true;
            all = [.. _subscriptions];
            _subscriptions.Clear();
        }
        foreach (var s in all) s.Dispose();
    }

    private void Remove(Subscription s)
    {
        lock (_subscriptions) _subscriptions.Remove(s);
    }

    private sealed class Subscription(PollingWindowEventSource owner, TargetWindowInfo initial, Action<WindowEvent> handler) : IDisposable
    {
        private readonly object _gate = new();
        private TargetWindowInfo? _last = initial;
        private nint _lastForeground = initial.IsForeground ? initial.Handle : 0;
        private ITimer? _timer;
        private bool _disposed;

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) return;
                // One-shot timer re-armed after each poll, so slow polls never overlap.
                _timer = owner._time.CreateTimer(static s => ((Subscription)s!).Tick(), this, owner.Interval, Timeout.InfiniteTimeSpan);
            }
        }

        internal void Tick()
        {
            try { Poll(); }
            catch (Exception ex) { owner._logger.LogWarning(ex, "Window poll failed"); }
            lock (_gate)
            {
                if (!_disposed && _last is not null) _timer?.Change(owner.Interval, Timeout.InfiniteTimeSpan);
            }
        }

        private void Poll()
        {
            TargetWindowInfo? previous;
            lock (_gate)
            {
                if (_disposed || _last is null) return;
                previous = _last;
            }
            var current = owner._windowSystem.GetWindow(previous.Handle);
            long now = owner._time.GetTimestamp();
            var events = new List<WindowEventKind>(4);
            if (current is null || current.ProcessId != previous.ProcessId)
            {
                events.Add(WindowEventKind.Destroyed);
                current = null;
            }
            else
            {
                if (current.IsMinimized != previous.IsMinimized) events.Add(current.IsMinimized ? WindowEventKind.Minimized : WindowEventKind.Restored);
                if (current.IsVisible != previous.IsVisible) events.Add(current.IsVisible ? WindowEventKind.Shown : WindowEventKind.Hidden);
                if (current.Bounds != previous.Bounds || current.ClientBounds != previous.ClientBounds || current.Dpi != previous.Dpi)
                    events.Add(WindowEventKind.LocationChanged);
            }

            nint foreground = current is null ? -1 : current.IsForeground ? current.Handle : 0;
            bool foregroundChanged = current is not null && foreground != _lastForeground;
            lock (_gate)
            {
                if (_disposed) return;
                _last = current;
                _lastForeground = foreground;
            }
            if (foregroundChanged) events.Add(WindowEventKind.ForegroundChanged);
            foreach (var kind in events)
            {
                try { handler(new WindowEvent(kind, previous.Handle, now)); }
                catch (Exception ex) { owner._logger.LogWarning(ex, "Window event handler threw for {Kind}", kind); }
            }
        }

        public void Dispose()
        {
            ITimer? timer;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                timer = _timer;
                _timer = null;
            }
            timer?.Dispose();
            owner.Remove(this);
        }
    }
}
