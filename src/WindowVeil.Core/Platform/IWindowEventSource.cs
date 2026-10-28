namespace WindowVeil.Platform;

/// <summary>Kinds of window notifications that can affect overlays.</summary>
public enum WindowEventKind
{
    /// <summary>The window moved or resized (including maximize/restore and DPI-driven changes).</summary>
    LocationChanged,

    /// <summary>The window was shown.</summary>
    Shown,

    /// <summary>The window was hidden.</summary>
    Hidden,

    /// <summary>The window was minimized.</summary>
    Minimized,

    /// <summary>The window was restored from minimized state.</summary>
    Restored,

    /// <summary>The window was destroyed.</summary>
    Destroyed,

    /// <summary>The foreground window changed (to this window or to another one).</summary>
    ForegroundChanged,

    /// <summary>Something inside the window changed (content, layout, scrolling); element geometry should be re-resolved.</summary>
    ContentChanged,

    /// <summary>The z-order of top-level windows changed.</summary>
    ZOrderChanged,
}

/// <summary>
/// A window notification. Delivered on an arbitrary thread; handlers must be fast and non-blocking.
/// <see cref="TimestampTicks"/> is a <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> value (monotonic, not wall-clock).
/// </summary>
public readonly record struct WindowEvent(WindowEventKind Kind, nint Handle, long TimestampTicks);

/// <summary>
/// Source of window change notifications (WinEvent hooks on Windows). Subscribers receive only cheap notifications;
/// all expensive work (UI Automation, OCR) must be queued elsewhere.
/// </summary>
public interface IWindowEventSource
{
    /// <summary>
    /// Subscribes to events for one window (and global foreground/z-order changes).
    /// Dispose the returned object to unsubscribe.
    /// </summary>
    IDisposable Subscribe(TargetWindowInfo window, Action<WindowEvent> handler);
}
