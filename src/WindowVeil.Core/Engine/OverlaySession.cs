using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Rendering;

namespace WindowVeil;

/// <summary>
/// The overlays attached to one target window. Rules can be added, updated, enabled, disabled and removed at any
/// time from any thread; changes are applied asynchronously by a single background loop that also tracks the target,
/// coalesces window events, re-resolves locators and reconnects after restarts.
/// </summary>
/// <remarks>
/// Events (<see cref="RuleStatusChanged"/>, <see cref="StateChanged"/>) are raised on the session's background
/// thread. Marshal to your UI thread if needed.
/// </remarks>
public sealed class OverlaySession : IAsyncDisposable
{
    [Flags]
    private enum Work
    {
        None = 0,
        Geometry = 1,
        Content = 2,
        ZOrder = 4,
        TargetCheck = 8,
        Rules = 16,
        Poll = 32,
        Reconnect = 64,
        All = Geometry | Content | ZOrder | TargetCheck | Rules,
    }

    private sealed class RuleEntry(OverlayRule rule, OverlayAppearance appearance, IOverlayRenderer renderer)
    {
        public OverlayRule Rule = rule;
        public OverlayAppearance Appearance = appearance;
        public IOverlayRenderer Renderer = renderer;
        public IOverlayVisual? Visual;
        public LocatorResolution? Resolution;
        public (DpiScale Dpi, PixelSize? Size)? Measured;
        public bool NeedsResolve = true;
        public bool CreationFailed;
        public RuleStatus Status = new() { RuleId = rule.Id, State = RuleState.Pending };

        // Last status published through RuleStatusChanged. Kept separately from Status because rule changes
        // (UpdateRule, Disable) update Status synchronously and the loop must still raise the event.
        public RuleStatus? Raised;
    }

    private readonly WindowVeilEngine _engine;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, RuleEntry> _rules = new(StringComparer.Ordinal);
    private readonly List<IOverlayVisual> _pendingDisposals = [];
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly List<(long Generation, TaskCompletionSource Tcs)> _flushWaiters = [];
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private int _pending;
    private long _requestGeneration;
    private TargetWindowInfo? _window;
    private IDisposable? _subscription;
    private SessionState _state;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private long _lastElementPoll;
    private long _lastTargetCheck;
    private long _lastReconnectAttempt;
    private string? _stateReason;
    private bool _everConnected;

    internal OverlaySession(WindowVeilEngine engine, WindowSelector selector, TargetWindowInfo? window, SessionOptions options, ILogger logger)
    {
        _engine = engine;
        Selector = selector;
        Options = options;
        _logger = logger;
        _window = window;
        _everConnected = window is not null;
        _state = window is null ? SessionState.WaitingForTarget : SessionState.Created;
    }

    /// <summary>The selector used to find (and re-find) the target.</summary>
    public WindowSelector Selector { get; }

    /// <summary>Options.</summary>
    public SessionOptions Options { get; }

    /// <summary>Latest snapshot of the target window (null while waiting for it).</summary>
    public TargetWindowInfo? Target { get { lock (_gate) return _window; } }

    /// <summary>Current state.</summary>
    public SessionState State { get { lock (_gate) return _state; } }

    /// <summary>Snapshot of all rules.</summary>
    public IReadOnlyList<OverlayRule> Rules { get { lock (_gate) return _rules.Values.Select(e => e.Rule).ToArray(); } }

    /// <summary>Snapshot of every rule's status.</summary>
    public IReadOnlyList<RuleStatus> RuleStatuses { get { lock (_gate) return _rules.Values.Select(e => e.Status).ToArray(); } }

    /// <summary>Raised when a rule's status changes (on the session thread).</summary>
    public event EventHandler<RuleStatusChangedEventArgs>? RuleStatusChanged;

    /// <summary>Raised when the session state changes (on the session thread or the caller's thread).</summary>
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    // ---------------------------------------------------------------- rule management

    /// <summary>
    /// Adds a rule. Validates it, resolves registered appearances and picks a renderer immediately, so configuration
    /// errors surface here rather than later.
    /// </summary>
    /// <exception cref="WindowVeilException">The rule is invalid, its appearance is unknown, no renderer supports it,
    /// or no resolver supports its locator.</exception>
    public OverlayRule AddRule(OverlayRule rule)
    {
        var entry = Prepare(rule);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_rules.TryAdd(rule.Id, entry))
                throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"A rule with id '{rule.Id}' already exists in this session.");
            entry.Status = entry.Raised = Status(entry, rule.Enabled ? RuleState.Pending : RuleState.Disabled);
        }
        Signal(Work.Rules);
        return rule;
    }

    /// <summary>Alias of <see cref="AddRule"/>.</summary>
    public OverlayRule AddOverlay(OverlayRule rule) => AddRule(rule);

    /// <summary>Adds several rules; if any is invalid, none are added.</summary>
    public void AddRules(IEnumerable<OverlayRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var list = rules.ToArray();
        var dupes = list.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (dupes.Length > 0) throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Duplicate rule ids: {string.Join(", ", dupes)}.");
        var entries = list.Select(Prepare).ToArray();
        lock (_gate)
        {
            ThrowIfDisposed();
            var existing = list.Where(r => _rules.ContainsKey(r.Id)).Select(r => r.Id).ToArray();
            if (existing.Length > 0) throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rules already exist: {string.Join(", ", existing)}.");
            foreach (var e in entries)
            {
                e.Status = e.Raised = Status(e, e.Rule.Enabled ? RuleState.Pending : RuleState.Disabled);
                _rules.Add(e.Rule.Id, e);
            }
        }
        Signal(Work.Rules);
    }

    /// <summary>Removes a rule and destroys its overlay. Returns false when the id is unknown.</summary>
    public bool RemoveRule(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        lock (_gate)
        {
            if (!_rules.Remove(ruleId, out var entry)) return false;
            if (entry.Visual is not null) _pendingDisposals.Add(entry.Visual);
            ReleaseState(entry.Resolution);
        }
        Signal(Work.Rules);
        return true;
    }

    /// <summary>Removes every rule.</summary>
    public void ClearRules()
    {
        lock (_gate)
        {
            foreach (var e in _rules.Values)
            {
                if (e.Visual is not null) _pendingDisposals.Add(e.Visual);
                ReleaseState(e.Resolution);
            }
            _rules.Clear();
        }
        Signal(Work.Rules);
    }

    /// <summary>
    /// Replaces the rule with the same id. Locator changes trigger re-resolution; appearance, input or z-order changes
    /// recreate the overlay; placement changes only move it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No rule has this id.</exception>
    public void UpdateRule(OverlayRule rule)
    {
        var prepared = Prepare(rule);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_rules.TryGetValue(rule.Id, out var entry)) throw new KeyNotFoundException($"No rule with id '{rule.Id}'.");
            var old = entry.Rule;
            bool recreate = !Equals(old.Appearance, rule.Appearance) || old.InputMode != rule.InputMode ||
                            old.FocusPolicy != rule.FocusPolicy || old.ZOrder != rule.ZOrder || !ReferenceEquals(entry.Renderer, prepared.Renderer);
            if (!Equals(old.Target, rule.Target))
            {
                ReleaseState(entry.Resolution);
                entry.Resolution = null;
            }
            if (!Equals(old.Target, rule.Target) || old.FollowTarget != rule.FollowTarget || (!old.Enabled && rule.Enabled))
                entry.NeedsResolve = true;
            if (!Equals(old.Placement, rule.Placement)) entry.Measured = null;
            entry.Rule = rule;
            entry.Appearance = prepared.Appearance;
            if (recreate)
            {
                if (entry.Visual is not null) _pendingDisposals.Add(entry.Visual);
                entry.Visual = null;
                entry.Measured = null;
                entry.Renderer = prepared.Renderer;
                entry.CreationFailed = false;
            }
            entry.Status = Status(entry, rule.Enabled ? (entry.Status.State == RuleState.Disabled ? RuleState.Pending : entry.Status.State) : RuleState.Disabled);
        }
        Signal(Work.Rules);
    }

    /// <summary>Enables a rule.</summary>
    public void EnableRule(string ruleId) => SetEnabled(ruleId, true);

    /// <summary>Disables a rule (its overlay is hidden but kept for fast re-enable).</summary>
    public void DisableRule(string ruleId) => SetEnabled(ruleId, false);

    /// <summary>Enables or disables every rule in a group. Returns the number of rules affected.</summary>
    public int SetGroupEnabled(string group, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(group);
        string[] ids;
        lock (_gate) ids = _rules.Values.Where(e => e.Rule.Group == group).Select(e => e.Rule.Id).ToArray();
        foreach (var id in ids) SetEnabled(id, enabled);
        return ids.Length;
    }

    /// <summary>Returns the rule with the given id, or null.</summary>
    public OverlayRule? GetRule(string ruleId) { lock (_gate) return _rules.TryGetValue(ruleId, out var e) ? e.Rule : null; }

    /// <summary>Returns a rule's status.</summary>
    /// <exception cref="KeyNotFoundException">No rule has this id.</exception>
    public RuleStatus GetRuleStatus(string ruleId)
    {
        lock (_gate) return _rules.TryGetValue(ruleId, out var e) ? e.Status : throw new KeyNotFoundException($"No rule with id '{ruleId}'.");
    }

    private void SetEnabled(string ruleId, bool enabled)
    {
        OverlayRule rule;
        lock (_gate)
        {
            if (!_rules.TryGetValue(ruleId, out var e)) throw new KeyNotFoundException($"No rule with id '{ruleId}'.");
            if (e.Rule.Enabled == enabled) return;
            rule = e.Rule with { Enabled = enabled };
        }
        UpdateRule(rule);
    }

    private RuleEntry Prepare(OverlayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();
        if (!_engine.Appearances.TryResolve(rule.Appearance, out var appearance, out var error))
            throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{rule.Id}': {error}");
        var renderer = _engine.FindRenderer(appearance!)
            ?? throw new WindowVeilException(WindowVeilErrorCode.RendererNotFound,
                $"Rule '{rule.Id}': no renderer supports '{appearance!.Kind}' appearances. Register the matching renderer (for example UseWpfOverlays()).");
        if (!_engine.Locators.Supports(rule.Target))
            throw new WindowVeilException(WindowVeilErrorCode.InvalidRule,
                $"Rule '{rule.Id}': no resolver supports '{rule.Target.Kind}' locators. Register the matching package (UI Automation or OCR).");
        return new RuleEntry(rule, appearance!, renderer);
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Starts tracking and shows overlays. Completes after the first update pass, so rule statuses are meaningful
    /// when it returns (an unresolvable rule is <see cref="RuleState.Unresolved"/>, not silently ignored).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_loop is not null) return;
            lock (_gate)
            {
                foreach (var e in _rules.Values) e.NeedsResolve = true;
            }
            if (_window is not null) { Subscribe(_window); SetState(SessionState.Running, "Started"); }
            else SetState(SessionState.WaitingForTarget, "Waiting for target window");
            _loopCts = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoopAsync(_loopCts.Token), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops tracking and destroys every overlay window. Rules are kept; call <see cref="StartAsync"/> to resume.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            if (State != SessionState.Disposed) SetState(SessionState.Stopped, "Stopped");
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Hides every overlay without destroying it and pauses resolution.</summary>
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state is not (SessionState.Running or SessionState.WaitingForTarget)) return;
            SetStateLocked(SessionState.Suspended, "Suspended");
        }
        RaiseStateChanged();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes after <see cref="SuspendAsync"/>.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state != SessionState.Suspended) return;
            SetStateLocked(_window is null ? SessionState.WaitingForTarget : SessionState.Running, "Resumed");
            foreach (var e in _rules.Values) e.NeedsResolve = true;
        }
        RaiseStateChanged();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forces every rule to be re-resolved and waits until the update pass that includes this request has completed.
    /// Requires the session to be started.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_state == SessionState.Disposed) return Task.CompletedTask;
            if (_loop is null) return Task.CompletedTask;
            foreach (var e in _rules.Values) e.NeedsResolve = true;
            _flushWaiters.Add((Interlocked.Increment(ref _requestGeneration), tcs));
        }
        Signal(Work.All);
        return tcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Waits until a rule's status satisfies <paramref name="predicate"/> or the timeout expires.</summary>
    /// <returns>The matching status.</returns>
    /// <exception cref="TimeoutException">The condition was not met in time.</exception>
    public async Task<RuleStatus> WaitForRuleAsync(string ruleId, Func<RuleStatus, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var tcs = new TaskCompletionSource<RuleStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, RuleStatusChangedEventArgs e) { if (e.Status.RuleId == ruleId && predicate(e.Status)) tcs.TrySetResult(e.Status); }
        RuleStatusChanged += Handler;
        try
        {
            var current = GetRuleStatus(ruleId);
            if (predicate(current)) return current;
            try
            {
                return await tcs.Task.WaitAsync(timeout, _engine.Options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"Rule '{ruleId}' did not reach the expected state within {timeout}. Last status: {GetRuleStatus(ruleId)}");
            }
        }
        finally { RuleStatusChanged -= Handler; }
    }

    /// <summary>Stops the session, destroys overlays and detaches from the engine.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == SessionState.Disposed) return;
            await StopCoreAsync().ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var e in _rules.Values) ReleaseState(e.Resolution);
                _rules.Clear();
                SetStateLocked(SessionState.Disposed, "Disposed");
            }
            RaiseStateChanged();
            _engine.OnSessionDisposed(this);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var cts = _loopCts;
        var loop = _loop;
        _loopCts = null;
        _loop = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try { if (loop is not null) await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            cts.Dispose();
        }
        _subscription?.Dispose();
        _subscription = null;

        List<IOverlayVisual> visuals;
        lock (_gate)
        {
            visuals = [.. _pendingDisposals];
            _pendingDisposals.Clear();
            foreach (var e in _rules.Values)
            {
                if (e.Visual is not null) visuals.Add(e.Visual);
                e.Visual = null;
                e.Measured = null;
                e.NeedsResolve = true;
                e.Status = Status(e, e.Rule.Enabled ? RuleState.Pending : RuleState.Disabled);
            }
            CompleteFlushWaiters(long.MaxValue);
        }
        await DisposeVisualsAsync(visuals).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- loop

    private void Signal(Work work)
    {
        Interlocked.Or(ref _pending, (int)work);
        _wake.Writer.TryWrite(0);
    }

    private void OnWindowEvent(WindowEvent e)
    {
        var work = e.Kind switch
        {
            WindowEventKind.LocationChanged => Work.Geometry,
            WindowEventKind.Shown or WindowEventKind.Hidden or WindowEventKind.Minimized or WindowEventKind.Restored => Work.Geometry,
            WindowEventKind.Destroyed => Work.TargetCheck | Work.Geometry,
            WindowEventKind.ForegroundChanged or WindowEventKind.ZOrderChanged => Work.ZOrder,
            WindowEventKind.ContentChanged => Work.Content,
            _ => Work.None,
        };
        if (work != Work.None) Signal(work);
    }

    private void Subscribe(TargetWindowInfo window)
    {
        _subscription?.Dispose();
        _subscription = null;
        if (_engine.Options.EventSource is { } source)
        {
            try { _subscription = source.Subscribe(window, OnWindowEvent); }
            catch (Exception ex) { _logger.LogWarning(ex, "Window event subscription failed; falling back to polling"); }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var time = _engine.Options.TimeProvider;
        _lastElementPoll = _lastTargetCheck = _lastReconnectAttempt = time.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            var delay = NextDelay();
            try
            {
                var readTask = _wake.Reader.WaitToReadAsync(ct).AsTask();
                if (!readTask.IsCompleted)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var delayTask = Task.Delay(delay, time, delayCts.Token);
                    await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
                    await delayCts.CancelAsync().ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            _wake.Reader.TryRead(out _);

            long generation = Interlocked.Read(ref _requestGeneration);
            var work = (Work)Interlocked.Exchange(ref _pending, 0);
            work |= TimedWork();
            try
            {
                await ProcessAsync(work, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Overlay session update failed");
            }
            lock (_gate) CompleteFlushWaiters(generation);
        }
    }

    private TimeSpan NextDelay()
    {
        var o = Options;
        var candidates = new List<TimeSpan> { o.TargetCheckInterval };
        if (o.ElementRefreshInterval > TimeSpan.Zero) candidates.Add(o.ElementRefreshInterval);
        if (_engine.Options.EventSource is null || _subscription is null) candidates.Add(o.GeometryPollInterval);
        if (State == SessionState.WaitingForTarget) candidates.Add(o.ReconnectPollInterval);
        var d = candidates.Where(c => c > TimeSpan.Zero).DefaultIfEmpty(TimeSpan.FromSeconds(1)).Min();
        return d < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : d;
    }

    private Work TimedWork()
    {
        var time = _engine.Options.TimeProvider;
        var now = time.GetTimestamp();
        var work = Work.None;
        if (_engine.Options.EventSource is null || _subscription is null) work |= Work.Geometry;
        if (time.GetElapsedTime(_lastTargetCheck, now) >= Options.TargetCheckInterval) { work |= Work.TargetCheck; _lastTargetCheck = now; }
        if (Options.ElementRefreshInterval > TimeSpan.Zero && time.GetElapsedTime(_lastElementPoll, now) >= Options.ElementRefreshInterval)
        { work |= Work.Poll; _lastElementPoll = now; }
        if (time.GetElapsedTime(_lastReconnectAttempt, now) >= Options.ReconnectPollInterval) { work |= Work.Reconnect; _lastReconnectAttempt = now; }
        return work;
    }

    private async Task ProcessAsync(Work work, CancellationToken ct)
    {
        List<IOverlayVisual> disposals;
        bool reconnected = false;
        lock (_gate) { disposals = [.. _pendingDisposals]; _pendingDisposals.Clear(); }
        await DisposeVisualsAsync(disposals).ConfigureAwait(false);

        var state = State;
        if (state is SessionState.Suspended or SessionState.TargetLost)
        {
            await HideAllAsync(state == SessionState.Suspended ? "Session suspended" : "Target lost", ct).ConfigureAwait(false);
            return;
        }
        if (state == SessionState.WaitingForTarget)
        {
            if ((work & (Work.Reconnect | Work.Rules | Work.TargetCheck)) == 0 && _everConnected) return;
            bool hadTarget = _everConnected;
            if (!TryReconnect()) { await HideAllAsync("Waiting for target window", ct).ConfigureAwait(false); return; }
            work |= Work.All;
            reconnected = hadTarget;
        }
        if (State != SessionState.Running) return;

        // Refresh the target snapshot.
        var old = _window!;
        var fresh = _engine.WindowSystem.GetWindow(old.Handle);
        if (fresh is null || (fresh.ProcessId != old.ProcessId) ||
            ((work & Work.TargetCheck) != 0 && !_engine.WindowSystem.IsProcessAlive(old.ProcessId)))
        {
            await OnTargetLostAsync(fresh is null ? "Target window was destroyed" : "Target process exited", ct).ConfigureAwait(false);
            return;
        }
        lock (_gate) _window = fresh;

        bool sizeChanged = fresh.Bounds.Size != old.Bounds.Size || fresh.ClientBounds.Size != old.ClientBounds.Size || fresh.Dpi != old.Dpi;
        int dx = fresh.Bounds.X - old.Bounds.X, dy = fresh.Bounds.Y - old.Bounds.Y;
        bool contentWork = (work & (Work.Content | Work.Poll)) != 0;

        RuleEntry[] entries;
        lock (_gate) entries = _rules.Values.OrderByDescending(e => e.Rule.Priority).ToArray();

        // Phase A: cheap updates (translate cached geometry on pure moves) and immediate render.
        foreach (var e in entries)
        {
            lock (_gate)
            {
                var rule = e.Rule;
                var resolution = e.Resolution;
                if (!rule.Enabled || !rule.FollowTarget || resolution is null) continue;
                if (!rule.Target.IsWindowDependent) continue;
                if (sizeChanged || rule.Target is WindowRelativeLocator or NormalizedLocator or FallbackLocator { Locators: [WindowRelativeLocator or NormalizedLocator] })
                    e.NeedsResolve = true;
                else if ((dx != 0 || dy != 0) && resolution.IsResolved)
                    e.Resolution = resolution.Translate(dx, dy);
                if (contentWork) e.NeedsResolve = true;
            }
        }
        // Z-order/foreground changes do not alter frames, but overlays must be re-stacked above the target.
        await RenderAsync(entries, fresh, ct, restack: (work & Work.ZOrder) != 0).ConfigureAwait(false);

        // Phase B: re-resolve where needed, then render again.
        if (!fresh.IsPresentable) return;
        bool anyResolved = false;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            bool needs;
            ElementLocator target;
            LocatorResolution? previous;
            lock (_gate)
            {
                needs = e.Rule.Enabled && e.NeedsResolve && IsCurrent(e) && (e.Rule.FollowTarget || e.Resolution is null || !e.Resolution.IsResolved);
                target = e.Rule.Target;
                previous = e.Resolution;
            }
            if (!needs) continue;
            var reason = previous is null
                ? (reconnected ? ResolveReason.Reconnected : ResolveReason.Initial)
                : sizeChanged ? ResolveReason.WindowChanged : (work & Work.Poll) != 0 ? ResolveReason.Poll : ResolveReason.ContentChanged;
            var result = await ResolveAsync(target, previous, fresh, reason, ct).ConfigureAwait(false);
            lock (_gate)
            {
                LocatorResolution? discard;
                if (!IsCurrent(e) || !Equals(e.Rule.Target, target))
                {
                    // The rule was removed or got a new locator while resolving: the result is stale. NeedsResolve keeps
                    // the value UpdateRule set, so the new locator is resolved on the next pass.
                    discard = result;
                }
                else
                {
                    e.NeedsResolve = false;
                    // OCR and other rate-limited resolvers return Cached: keep the previous geometry.
                    if (result.Status == LocatorStatus.Cached && e.Resolution is { IsResolved: true })
                    {
                        discard = result;
                    }
                    else
                    {
                        discard = e.Resolution;
                        e.Resolution = result;
                    }
                }
                // Release resolver state that is no longer referenced (state reused by the kept resolution survives).
                ReleaseState(discard, keep: e.Resolution);
            }
            anyResolved = true;
        }
        if (anyResolved)
        {
            var current = _engine.WindowSystem.GetWindow(fresh.Handle) ?? fresh;
            lock (_gate) _window = current;
            await RenderAsync(entries, current, ct).ConfigureAwait(false);
        }
    }

    private async Task<LocatorResolution> ResolveAsync(ElementLocator target, LocatorResolution? previous, TargetWindowInfo window, ResolveReason reason, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.ResolveTimeout);
        var overlays = OverlayWindowRegistry.Snapshot();
        var context = new LocatorContext(window, reason)
        {
            Previous = previous,
            Capture = _engine.Options.Capture,
            Logger = _logger,
            OverlayWindows = overlays,
            Time = _engine.Options.TimeProvider,
        };
        try
        {
            return await _engine.Locators.ResolveAsync(target, context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LocatorResolution.Failed(target, LocatorStatus.Error, $"Resolution timed out after {Options.ResolveTimeout}.");
        }
    }

    private async Task RenderAsync(RuleEntry[] entries, TargetWindowInfo window, CancellationToken ct, bool restack = false)
    {
        var batches = new Dictionary<IOverlayRenderer, List<OverlayVisualUpdate>>();
        var statusChanges = new List<RuleStatus>();
        foreach (var e in entries)
        {
            // Rule changes run concurrently on other threads: read each mutable field once.
            OverlayRule rule;
            LocatorResolution? resolution;
            IOverlayVisual? visual;
            lock (_gate)
            {
                if (!IsCurrent(e)) continue;
                rule = e.Rule;
                resolution = e.Resolution;
                visual = e.Visual;
            }

            RuleState state;
            string? message = null;
            WindowVeilErrorCode? error = null;
            OverlayFrame frame = OverlayFrame.Hidden;

            if (!rule.Enabled) state = RuleState.Disabled;
            else if (resolution is null) state = RuleState.Pending;
            else if (!resolution.IsResolved)
            {
                state = RuleState.Unresolved;
                message = resolution.Message;
                error = resolution.Status switch
                {
                    LocatorStatus.Unavailable when resolution.ResolvedBy is OcrTextLocator => WindowVeilErrorCode.OcrUnavailable,
                    LocatorStatus.Unavailable when resolution.ResolvedBy is UiAutomationLocator or TextLocator => WindowVeilErrorCode.UiAutomationUnavailable,
                    LocatorStatus.Unavailable => WindowVeilErrorCode.CaptureUnavailable,
                    LocatorStatus.Rejected when resolution.ResolvedBy is OcrTextLocator => WindowVeilErrorCode.OcrConfidenceTooLow,
                    LocatorStatus.Error => WindowVeilErrorCode.Unknown,
                    _ => WindowVeilErrorCode.ElementNotFound,
                };
            }
            else if (!window.IsPresentable) { state = RuleState.Hidden; message = window.IsMinimized ? "Target minimized" : "Target hidden"; }
            else if (rule.Visibility == OverlayVisibility.WhenTargetForeground && !window.IsForeground) { state = RuleState.Hidden; message = "Target is not in the foreground"; }
            else
            {
                if (visual is null && !e.CreationFailed)
                {
                    try
                    {
                        var created = await e.Renderer.CreateVisualAsync(new OverlayVisualRequest(rule.Id, e.Appearance)
                        {
                            InputMode = rule.InputMode, FocusPolicy = rule.FocusPolicy, ZOrder = rule.ZOrder,
                        }, ct).ConfigureAwait(false);
                        bool keep;
                        lock (_gate)
                        {
                            keep = IsCurrent(e) && e.Visual is null && ReferenceEquals(e.Rule, rule);
                            if (keep) e.Visual = created;
                        }
                        if (!keep) { await DisposeVisualsAsync([created]).ConfigureAwait(false); continue; }
                        visual = created;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        e.CreationFailed = true;
                        _logger.LogError(ex, "Failed to create overlay for rule {RuleId}", rule.Id);
                        Publish(e, Status(e, RuleState.Failed, ex.Message, ex is WindowVeilException wv ? wv.Code : WindowVeilErrorCode.OverlayCreationFailed), statusChanges);
                        continue;
                    }
                }
                if (visual is null) { state = RuleState.Failed; error = WindowVeilErrorCode.OverlayCreationFailed; message = e.Status.Message; }
                else
                {
                    PixelSize? size = null;
                    if (!rule.Placement.SizesToTarget && (rule.Placement.Width is null || rule.Placement.Height is null))
                    {
                        if (e.Measured is not { } m || m.Dpi != window.Dpi)
                        {
                            try { e.Measured = (window.Dpi, await visual.MeasureAsync(window.Dpi, ct).ConfigureAwait(false)); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Measure failed for {RuleId}", rule.Id); e.Measured = (window.Dpi, null); }
                        }
                        size = e.Measured?.Size;
                    }
                    var regions = PlacementCalculator.Calculate(resolution.Bounds, rule.Placement, window.Dpi, size);
                    frame = OverlayFrame.Show(regions, resolution.Bounds, window);
                    state = frame.Visible ? RuleState.Active : RuleState.Hidden;
                }
            }

            if (visual is not null && (!frame.IsEquivalentTo(visual.CurrentFrame) || (restack && frame.Visible)) &&
                !(frame.Visible == false && (visual.CurrentFrame is null || !visual.CurrentFrame.Visible)))
            {
                if (!batches.TryGetValue(e.Renderer, out var list)) batches[e.Renderer] = list = [];
                list.Add(new OverlayVisualUpdate(visual, frame));
            }

            Publish(e, Status(e, state, message, error, frame.Visible ? frame.Regions : []), statusChanges);
        }

        foreach (var (renderer, updates) in batches)
        {
            try { await renderer.ApplyAsync(updates, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Renderer {Renderer} failed to apply {Count} updates", renderer.Name, updates.Count);
                foreach (var u in updates)
                {
                    RuleEntry? e;
                    lock (_gate) _rules.TryGetValue(u.Visual.RuleId, out e);
                    if (e is null) continue;
                    Publish(e, Status(e, RuleState.Failed, ex.Message, WindowVeilErrorCode.OverlayCreationFailed), statusChanges);
                }
            }
        }

        foreach (var s in statusChanges) RaiseRuleStatus(s);
    }

    private async Task HideAllAsync(string reason, CancellationToken ct)
    {
        RuleEntry[] entries;
        lock (_gate) entries = _rules.Values.ToArray();
        var batches = new Dictionary<IOverlayRenderer, List<OverlayVisualUpdate>>();
        var changes = new List<RuleStatus>();
        foreach (var e in entries)
        {
            if (e.Visual?.CurrentFrame is { Visible: true })
            {
                if (!batches.TryGetValue(e.Renderer, out var list)) batches[e.Renderer] = list = [];
                list.Add(new OverlayVisualUpdate(e.Visual, OverlayFrame.Hidden));
            }
            var state = e.Rule.Enabled ? RuleState.Hidden : RuleState.Disabled;
            Publish(e, Status(e, state, e.Rule.Enabled ? reason : null), changes);
        }
        foreach (var (renderer, updates) in batches)
        {
            try { await renderer.ApplyAsync(updates, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Failed to hide overlays"); }
        }
        foreach (var s in changes) RaiseRuleStatus(s);
    }

    private async Task OnTargetLostAsync(string reason, CancellationToken ct)
    {
        _logger.LogInformation("Target lost: {Reason}", reason);
        _subscription?.Dispose();
        _subscription = null;
        lock (_gate)
        {
            foreach (var e in _rules.Values)
            {
                ReleaseState(e.Resolution);
                e.Resolution = null;
                e.NeedsResolve = true;
            }
            _window = null;
            bool canReconnect = Options.AutoReconnect && Selector.Handle is null;
            SetStateLocked(canReconnect ? SessionState.WaitingForTarget : SessionState.TargetLost, reason);
        }
        RaiseStateChanged();
        await HideAllAsync(reason, ct).ConfigureAwait(false);
    }

    private bool TryReconnect()
    {
        var candidates = _engine.WindowSystem.EnumerateWindows()
            .Where(w => !OverlayWindowRegistry.IsOverlay(w.Handle))
            .Where(w => _engine.WindowSystem.IsProcessAlive(w.ProcessId)) // a dying process can leave its window listed briefly
            .ToArray();
        var found = Selector.SelectBest(candidates);
        if (found is null) return false;
        lock (_gate)
        {
            _window = found;
            foreach (var e in _rules.Values) { e.NeedsResolve = true; ReleaseState(e.Resolution); e.Resolution = null; }
            SetStateLocked(SessionState.Running, _everConnected ? "Target reconnected" : "Target found");
            _everConnected = true;
        }
        Subscribe(found);
        _logger.LogInformation("Connected to {Window}", found);
        RaiseStateChanged();
        return true;
    }

    // ---------------------------------------------------------------- helpers

    private static RuleStatus Status(RuleEntry e, RuleState state, string? message = null, WindowVeilErrorCode? error = null, IReadOnlyList<PixelRect>? bounds = null) => new()
    {
        RuleId = e.Rule.Id,
        State = state,
        Resolution = e.Resolution,
        OverlayBounds = bounds ?? [],
        Message = message,
        Error = error,
        Renderer = e.Renderer.Name,
        OverlayHandle = e.Visual?.Handle ?? 0,
    };

    /// <summary>Stores a computed status and queues an event when it differs from the last published one.</summary>
    private void Publish(RuleEntry e, RuleStatus status, List<RuleStatus> changes)
    {
        lock (_gate)
        {
            e.Status = status;
            if (e.Raised is not null && SameStatus(status, e.Raised)) return;
            e.Raised = status;
        }
        changes.Add(status);
    }

    /// <summary>Whether the entry is still the live entry for its rule id (not removed or replaced). Call under the lock.</summary>
    private bool IsCurrent(RuleEntry e) => _rules.TryGetValue(e.Rule.Id, out var cur) && ReferenceEquals(cur, e);

    private static bool SameStatus(RuleStatus a, RuleStatus b) =>
        a.State == b.State && a.Error == b.Error && a.Message == b.Message && a.OverlayHandle == b.OverlayHandle &&
        a.OverlayBounds.SequenceEqual(b.OverlayBounds) && ReferenceEquals(a.Resolution, b.Resolution);

    private void RaiseRuleStatus(RuleStatus status)
    {
        try { RuleStatusChanged?.Invoke(this, new RuleStatusChangedEventArgs(status)); }
        catch (Exception ex) { _logger.LogWarning(ex, "RuleStatusChanged handler threw"); }
    }

    private SessionState _raisedState;

    private void SetState(SessionState state, string reason)
    {
        lock (_gate) SetStateLocked(state, reason);
        RaiseStateChanged();
    }

    private void SetStateLocked(SessionState state, string reason)
    {
        _state = state;
        _stateReason = reason;
    }

    private void RaiseStateChanged()
    {
        SessionState old, now;
        string? reason;
        lock (_gate)
        {
            old = _raisedState; now = _state; reason = _stateReason;
            if (old == now) return;
            _raisedState = now;
        }
        try { StateChanged?.Invoke(this, new SessionStateChangedEventArgs(old, now, reason)); }
        catch (Exception ex) { _logger.LogWarning(ex, "StateChanged handler threw"); }
    }

    private void CompleteFlushWaiters(long generation)
    {
        for (int i = _flushWaiters.Count - 1; i >= 0; i--)
        {
            if (_flushWaiters[i].Generation <= generation)
            {
                _flushWaiters[i].Tcs.TrySetResult();
                _flushWaiters.RemoveAt(i);
            }
        }
    }

    private async Task DisposeVisualsAsync(IEnumerable<IOverlayVisual> visuals)
    {
        foreach (var v in visuals)
        {
            try { await v.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose overlay for rule {RuleId}", v.RuleId); }
        }
    }

    /// <summary>
    /// Disposes resolver state held by <paramref name="resolution"/> and its attempts, except state that is still
    /// referenced by <paramref name="keep"/> (resolvers may hand the previous state to the next resolution).
    /// </summary>
    private static void ReleaseState(LocatorResolution? resolution, LocatorResolution? keep = null)
    {
        if (resolution is null || ReferenceEquals(resolution, keep)) return;
        var kept = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (keep is not null)
            foreach (var r in keep.Attempts.Append(keep))
                if (r.State is not null) kept.Add(r.State);
        foreach (var r in resolution.Attempts.Append(resolution))
            if (r.State is IDisposable d && kept.Add(d)) { try { d.Dispose(); } catch (ObjectDisposedException) { } }
    }

    private void ThrowIfDisposed()
    {
        if (_state == SessionState.Disposed) throw new ObjectDisposedException(nameof(OverlaySession));
    }
}
