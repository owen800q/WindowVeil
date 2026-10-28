using WindowVeil.Geometry;
using WindowVeil.Locators;

namespace WindowVeil;

/// <summary>Lifecycle state of an <see cref="OverlaySession"/>.</summary>
public enum SessionState
{
    /// <summary>Attached but not started; rules can be added.</summary>
    Created,

    /// <summary>Tracking the target and showing overlays.</summary>
    Running,

    /// <summary>Temporarily paused; overlays are hidden, rules and resources are kept.</summary>
    Suspended,

    /// <summary>The target window is gone; waiting for it to reappear (automatic reconnection).</summary>
    WaitingForTarget,

    /// <summary>The target is gone and reconnection is disabled or impossible.</summary>
    TargetLost,

    /// <summary>Stopped; overlays destroyed. Can be started again.</summary>
    Stopped,

    /// <summary>Disposed; no further use is possible.</summary>
    Disposed,
}

/// <summary>Per-rule state.</summary>
public enum RuleState
{
    /// <summary>Not yet processed.</summary>
    Pending,

    /// <summary>Resolved and the overlay is shown.</summary>
    Active,

    /// <summary>Resolved but hidden (target minimized, not foreground, session suspended, ...).</summary>
    Hidden,

    /// <summary>The locator did not resolve; no overlay is shown. See <see cref="RuleStatus.Resolution"/>.</summary>
    Unresolved,

    /// <summary>The rule is disabled.</summary>
    Disabled,

    /// <summary>The overlay could not be created or updated.</summary>
    Failed,
}

/// <summary>A snapshot of a rule's state.</summary>
public sealed record RuleStatus
{
    /// <summary>Rule id.</summary>
    public required string RuleId { get; init; }

    /// <summary>State.</summary>
    public required RuleState State { get; init; }

    /// <summary>Latest locator resolution, including which strategy resolved it.</summary>
    public LocatorResolution? Resolution { get; init; }

    /// <summary>Overlay rectangles currently applied (physical screen pixels; empty when hidden).</summary>
    public IReadOnlyList<PixelRect> OverlayBounds { get; init; } = [];

    /// <summary>Error category for <see cref="RuleState.Failed"/> and <see cref="RuleState.Unresolved"/>.</summary>
    public WindowVeilErrorCode? Error { get; init; }

    /// <summary>Diagnostic message.</summary>
    public string? Message { get; init; }

    /// <summary>Renderer used for the overlay.</summary>
    public string? Renderer { get; init; }

    /// <summary>Overlay host window handle (0 if none).</summary>
    public nint OverlayHandle { get; init; }

    /// <summary>When this status was produced.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public override string ToString() =>
        $"{RuleId}: {State}{(Resolution?.ResolvedBy is { } by ? " via " + by.Kind : "")}{(OverlayBounds.Count > 0 ? " " + PixelRect.UnionAll(OverlayBounds) : "")}{(Message is null ? "" : " - " + Message)}";
}

/// <summary>Arguments for <see cref="OverlaySession.RuleStatusChanged"/>.</summary>
public sealed class RuleStatusChangedEventArgs(RuleStatus status) : EventArgs
{
    /// <summary>The new status.</summary>
    public RuleStatus Status { get; } = status;
}

/// <summary>Arguments for <see cref="OverlaySession.StateChanged"/>.</summary>
public sealed class SessionStateChangedEventArgs(SessionState oldState, SessionState newState, string? reason) : EventArgs
{
    /// <summary>Previous state.</summary>
    public SessionState OldState { get; } = oldState;

    /// <summary>New state.</summary>
    public SessionState NewState { get; } = newState;

    /// <summary>Why the state changed.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>Session behavior options.</summary>
public sealed record SessionOptions
{
    /// <summary>Reconnect automatically when the target closes and a matching window appears again. Default true.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>How often to look for the target while waiting to reconnect. Default 1 second.</summary>
    public TimeSpan ReconnectPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often element-based locators are re-resolved to pick up layout changes and scrolling that produce no
    /// window events (for example inside WPF windows, which have no child HWNDs). <see cref="TimeSpan.Zero"/> disables polling.
    /// Default 500 ms.
    /// </summary>
    public TimeSpan ElementRefreshInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Fallback geometry poll used when no window event source is available. Default 100 ms.</summary>
    public TimeSpan GeometryPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How often the target's liveness is verified independently of events. Default 1 second.</summary>
    public TimeSpan TargetCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for one locator resolution. Default 5 seconds.</summary>
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="WindowVeilEngine.AttachAsync(Platform.WindowSelector, SessionOptions?, CancellationToken)"/> waits for a matching window. Default zero (fail immediately).</summary>
    public TimeSpan AttachTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// When true, attaching succeeds even if no window matches yet; the session starts in
    /// <see cref="SessionState.WaitingForTarget"/>. Default false (an explicit error is raised instead).
    /// </summary>
    public bool AllowDeferredAttach { get; init; }
}
