using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Native;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using static WindowVeil.Tracking.Interop.TrackingNativeMethods;

namespace WindowVeil.Tracking;

/// <summary>
/// <see cref="IWindowEventSource"/> based on out-of-context WinEvent hooks (<c>SetWinEventHook</c> with
/// <c>WINEVENT_OUTOFCONTEXT</c>): no DLL is injected into the target; the system posts each event to the hooking thread,
/// which must pump messages. Hooks are therefore installed, served and removed on a <see cref="NativeMessageThread"/>.
/// </summary>
/// <remarks>
/// <para><b>Hook sharing.</b> Hooks are installed per target <i>process</i> and reference-counted: every subscription to
/// a window of process P shares P's hooks, which are removed when the last such subscription is disposed. One global
/// <c>EVENT_SYSTEM_FOREGROUND</c> hook (also reference-counted) serves every subscription. Narrow event ranges are used so
/// the target does not post unrelated events (selection, focus, help) to WindowVeil.</para>
/// <para><b>Own process.</b> <c>WINEVENT_SKIPOWNPROCESS</c> is used for hooks on other processes. It is deliberately not
/// used for the foreground hook (WindowVeil must notice when one of its host application's windows takes the foreground
/// from the target) nor for hooks on WindowVeil's own process (in-process targets); WindowVeil overlays are filtered with
/// <see cref="OverlayWindowRegistry"/> instead.</para>
/// <para><b>Callback cost.</b> The callback only filters (handle comparisons, at most one <c>GetAncestor</c> call), maps the
/// event and invokes the subscriber. <see cref="WindowEventKind.ContentChanged"/> is throttled per subscription
/// (leading and trailing edge, see <see cref="ContentChangedThrottle"/>). Events are delivered on the message thread;
/// handlers must not block.</para>
/// <para><see cref="WindowEvent.TimestampTicks"/> is a <see cref="Stopwatch.GetTimestamp"/> value.</para>
/// </remarks>
public sealed class WinEventWindowEventSource : IWindowEventSource, IDisposable
{
    private static readonly ConcurrentDictionary<nint, WinEventWindowEventSource> HookOwners = new();

    // Per-process event ranges: [min, max] pairs.
    private static readonly (uint Min, uint Max)[] ProcessRanges =
    [
        (EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND),
        (EVENT_SYSTEM_SCROLLINGEND, EVENT_SYSTEM_SCROLLINGEND),
        (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
        (EVENT_OBJECT_DESTROY, EVENT_OBJECT_REORDER), // destroy, show, hide, reorder
        (EVENT_OBJECT_STATECHANGE, EVENT_OBJECT_NAMECHANGE), // state, location, name
        (EVENT_OBJECT_VALUECHANGE, EVENT_OBJECT_VALUECHANGE),
        (EVENT_OBJECT_CONTENTSCROLLED, EVENT_OBJECT_CONTENTSCROLLED),
    ];

    private readonly NativeMessageThread _thread;
    private readonly bool _ownsThread;
    private readonly ILogger _logger;

    // Message-thread state.
    private readonly Dictionary<uint, ProcessHooks> _processHooks = [];
    private readonly Dictionary<nint, Subscription[]> _byWindow = [];
    private Subscription[] _all = [];
    private nint _foregroundHook;
    private int _disposeState;

    /// <summary>Creates a source with its own message thread (disposed with the source).</summary>
    public WinEventWindowEventSource(ILogger? logger = null)
    {
        _thread = new NativeMessageThread("WindowVeil WinEvent tracking");
        _ownsThread = true;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Creates a source that installs its hooks on a shared message thread (not disposed with the source).</summary>
    public WinEventWindowEventSource(NativeMessageThread thread, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ObjectDisposedException.ThrowIf(thread.IsDisposed, thread);
        _thread = thread;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Minimum interval between two <see cref="WindowEventKind.ContentChanged"/> notifications per subscription
    /// (default 50 ms). Bursts are collapsed into one leading and one trailing notification.
    /// </summary>
    public TimeSpan ContentChangedThrottle { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The thread that owns the hooks and receives the callbacks.</summary>
    public NativeMessageThread MessageThread => _thread;

    /// <summary>Number of installed WinEvent hooks (diagnostics).</summary>
    public int HookCount => HookOwners.Count(kv => ReferenceEquals(kv.Value, this));

    /// <inheritdoc />
    /// <exception cref="WindowVeilException"><see cref="WindowVeilErrorCode.PlatformUnavailable"/> when a hook cannot be installed.</exception>
    public IDisposable Subscribe(TargetWindowInfo window, Action<WindowEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();
        var subscription = new Subscription(this, window.Handle, (uint)window.ProcessId, handler);
        _thread.Invoke(() => AddOnThread(subscription));
        return subscription;
    }

    /// <summary>Removes every hook. Pending subscriptions stop receiving events.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        try
        {
            _thread.Invoke(() =>
            {
                foreach (var s in _all) s.MarkDisposed();
                foreach (var ph in _processHooks.Values) Unhook(ph.Hooks);
                _processHooks.Clear();
                if (_foregroundHook != 0) Unhook([_foregroundHook]);
                _foregroundHook = 0;
                _byWindow.Clear();
                _all = [];
            });
        }
        catch (ObjectDisposedException)
        {
            // The shared thread is gone; its hooks went with it. Drop the bookkeeping.
            foreach (var kv in HookOwners.Where(kv => ReferenceEquals(kv.Value, this)).ToArray()) HookOwners.TryRemove(kv);
        }
        if (_ownsThread) _thread.Dispose();
    }

    // ------------------------------------------------------------------ hook management (message thread)

    private sealed class ProcessHooks(nint[] hooks)
    {
        public readonly nint[] Hooks = hooks;
        public int Refs;
    }

    private void AddOnThread(Subscription s)
    {
        ThrowIfDisposed();
        bool newProcess = false;
        if (!_processHooks.TryGetValue(s.ProcessId, out var ph))
        {
            ph = new ProcessHooks(InstallProcessHooks(s.ProcessId));
            _processHooks[s.ProcessId] = ph;
            newProcess = true;
        }
        if (_foregroundHook == 0)
        {
            try
            {
                // Global (all processes). Not WINEVENT_SKIPOWNPROCESS: see the class remarks.
                _foregroundHook = Install(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, WINEVENT_OUTOFCONTEXT);
            }
            catch
            {
                if (newProcess) { Unhook(ph.Hooks); _processHooks.Remove(s.ProcessId); }
                throw;
            }
        }
        ph.Refs++;
        _byWindow[s.Handle] = _byWindow.TryGetValue(s.Handle, out var list) ? [.. list, s] : [s];
        _all = [.. _all, s];
    }

    private void RemoveOnThread(Subscription s)
    {
        if (Volatile.Read(ref _disposeState) != 0) return;
        if (!_all.Contains(s)) return;
        _all = _all.Where(x => !ReferenceEquals(x, s)).ToArray();
        if (_byWindow.TryGetValue(s.Handle, out var list))
        {
            var remaining = list.Where(x => !ReferenceEquals(x, s)).ToArray();
            if (remaining.Length == 0) _byWindow.Remove(s.Handle);
            else _byWindow[s.Handle] = remaining;
        }
        if (_processHooks.TryGetValue(s.ProcessId, out var ph) && --ph.Refs <= 0)
        {
            Unhook(ph.Hooks);
            _processHooks.Remove(s.ProcessId);
        }
        if (_all.Length == 0 && _foregroundHook != 0)
        {
            Unhook([_foregroundHook]);
            _foregroundHook = 0;
        }
    }

    private nint[] InstallProcessHooks(uint processId)
    {
        // Skipping our own process is only correct when the target lives elsewhere.
        uint flags = WINEVENT_OUTOFCONTEXT | (processId == (uint)Environment.ProcessId ? 0 : WINEVENT_SKIPOWNPROCESS);
        var hooks = new List<nint>(ProcessRanges.Length);
        try
        {
            foreach (var (min, max) in ProcessRanges) hooks.Add(Install(min, max, processId, flags));
            return [.. hooks];
        }
        catch
        {
            Unhook(hooks);
            throw;
        }
    }

    private unsafe nint Install(uint min, uint max, uint processId, uint flags)
    {
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback = &WinEventCallback;
        nint hook = SetWinEventHook(min, max, 0, (nint)callback, processId, 0, flags);
        if (hook == 0)
        {
            throw new WindowVeilException(WindowVeilErrorCode.PlatformUnavailable,
                $"SetWinEventHook(0x{min:X4}..0x{max:X4}) failed for process {processId} (error {Marshal.GetLastPInvokeError()}).");
        }
        // No callback can run before this is recorded: callbacks are delivered by this thread's message loop.
        HookOwners[hook] = this;
        return hook;
    }

    private void Unhook(IEnumerable<nint> hooks)
    {
        foreach (var hook in hooks)
        {
            HookOwners.TryRemove(hook, out _);
            if (!UnhookWinEvent(hook)) _logger.LogDebug("UnhookWinEvent(0x{Hook:X}) failed", hook);
        }
    }

    // ------------------------------------------------------------------ callback (message thread)

    [UnmanagedCallersOnly]
    private static void WinEventCallback(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            if (HookOwners.TryGetValue(hook, out var source)) source.OnEvent(eventType, hwnd, idObject, idChild);
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into user32.
            try { Trace.TraceError("WindowVeil WinEvent callback failed: {0}", ex); } catch { }
        }
    }

    private void OnEvent(uint eventType, nint hwnd, int idObject, int idChild)
    {
        if (hwnd == 0 || Volatile.Read(ref _disposeState) != 0) return;
        long now = Stopwatch.GetTimestamp();

        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            foreach (var s in _all) s.Deliver(WindowEventKind.ForegroundChanged, hwnd, now);
            return;
        }
        if (idObject is OBJID_CARET or OBJID_CURSOR) return;
        if (OverlayWindowRegistry.IsOverlay(hwnd)) return;

        if (_byWindow.TryGetValue(hwnd, out var direct))
        {
            bool windowItself = idObject == OBJID_WINDOW && idChild == CHILDID_SELF;
            WindowEventKind? kind = windowItself ? MapWindowEvent(eventType) : null;
            if (kind is { } k)
            {
                foreach (var s in direct) s.Deliver(k, hwnd, now);
            }
            else
            {
                // Title/state changes of the window itself, or events of its accessible children (list items,
                // scroll bars) — content may have moved.
                foreach (var s in direct) s.DeliverContentChanged(now);
            }
            return;
        }

        if (_byWindow.Count == 0) return;
        // Child windows (controls) of a target: anything that changes them can change element geometry.
        nint root = GetAncestor(hwnd, GA_ROOT);
        if (root == 0 || root == hwnd || !_byWindow.TryGetValue(root, out var owners)) return;
        foreach (var s in owners) s.DeliverContentChanged(now);
    }

    /// <summary>Maps an event on the target window object itself (OBJID_WINDOW, CHILDID_SELF).</summary>
    private static WindowEventKind? MapWindowEvent(uint eventType) => eventType switch
    {
        EVENT_OBJECT_LOCATIONCHANGE or EVENT_SYSTEM_MOVESIZESTART or EVENT_SYSTEM_MOVESIZEEND => WindowEventKind.LocationChanged,
        EVENT_OBJECT_SHOW => WindowEventKind.Shown,
        EVENT_OBJECT_HIDE => WindowEventKind.Hidden,
        EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
        EVENT_SYSTEM_MINIMIZESTART => WindowEventKind.Minimized,
        EVENT_SYSTEM_MINIMIZEEND => WindowEventKind.Restored,
        EVENT_OBJECT_REORDER => WindowEventKind.ZOrderChanged,
        _ => null, // name/state/value/scroll changes of the window itself: content
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    // ------------------------------------------------------------------ subscription

    private sealed class Subscription : IDisposable
    {
        private readonly WinEventWindowEventSource _source;
        private readonly Action<WindowEvent> _handler;
        private readonly long _throttleTicks;
        private Timer? _trailing;
        private long _lastContent = long.MinValue / 2;
        private int _trailingArmed;
        private int _disposed;

        public Subscription(WinEventWindowEventSource source, nint handle, uint processId, Action<WindowEvent> handler)
        {
            _source = source;
            Handle = handle;
            ProcessId = processId;
            _handler = handler;
            _throttleTicks = (long)(source.ContentChangedThrottle.TotalSeconds * Stopwatch.Frequency);
        }

        public nint Handle { get; }

        public uint ProcessId { get; }

        public void Deliver(WindowEventKind kind, nint hwnd, long timestamp)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _handler(new WindowEvent(kind, hwnd, timestamp)); }
            catch (Exception ex) { _source._logger.LogWarning(ex, "Window event handler threw for {Kind}", kind); }
        }

        /// <summary>Leading-edge delivery plus one trailing delivery for bursts, at most one per throttle interval.</summary>
        public void DeliverContentChanged(long now)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            long elapsed = now - Volatile.Read(ref _lastContent);
            if (elapsed >= _throttleTicks)
            {
                Volatile.Write(ref _lastContent, now);
                Deliver(WindowEventKind.ContentChanged, Handle, now);
                return;
            }
            if (Interlocked.Exchange(ref _trailingArmed, 1) != 0) return;
            var dueMs = Math.Max(1, (int)Math.Ceiling((_throttleTicks - elapsed) * 1000.0 / Stopwatch.Frequency));
            try
            {
                _trailing ??= new Timer(static s => ((Subscription)s!).OnTrailing(), this, Timeout.Infinite, Timeout.Infinite);
                _trailing.Change(dueMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException) { }
        }

        private void OnTrailing()
        {
            Volatile.Write(ref _trailingArmed, 0);
            long now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lastContent, now);
            Deliver(WindowEventKind.ContentChanged, Handle, now);
        }

        public void MarkDisposed()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _trailing?.Dispose();
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            MarkDisposed();
            var thread = _source._thread;
            if (thread.CheckAccess())
            {
                _source.RemoveOnThread(this);
                return;
            }
            // Non-blocking: unhooking happens asynchronously on the message thread; no events reach the handler anymore.
            _ = thread.InvokeAsync(() => _source.RemoveOnThread(this)).ContinueWith(
                static (t, state) =>
                {
                    if (t.Exception?.InnerException is { } ex and not ObjectDisposedException)
                        ((ILogger)state!).LogWarning(ex, "Failed to remove WinEvent subscription");
                },
                _source._logger, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
