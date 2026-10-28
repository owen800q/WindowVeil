using WindowVeil.Geometry;

namespace WindowVeil.Platform;

/// <summary>
/// A snapshot of an external top-level window. All rectangles are physical virtual-desktop screen pixels.
/// Snapshots are immutable; ask <see cref="IWindowSystem"/> for a fresh one after the window changes.
/// </summary>
public sealed record TargetWindowInfo
{
    /// <summary>Native window handle (HWND). Handles are only valid for the lifetime of the window.</summary>
    public required nint Handle { get; init; }

    /// <summary>Owning process id.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Owning process name without extension (e.g. <c>RetroCap</c>), or empty when unavailable.</summary>
    public string ProcessName { get; init; } = "";

    /// <summary>Window title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Window class name.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>
    /// Visible outer bounds. On Windows this is the DWM extended frame bounds when available, which excludes the
    /// invisible resize borders included by <c>GetWindowRect</c>.
    /// </summary>
    public required PixelRect Bounds { get; init; }

    /// <summary>Client-area bounds in screen coordinates.</summary>
    public required PixelRect ClientBounds { get; init; }

    /// <summary>Whether the window has the visible style and is not cloaked.</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>Whether the window is minimized.</summary>
    public bool IsMinimized { get; init; }

    /// <summary>Whether the window is the foreground window.</summary>
    public bool IsForeground { get; init; }

    /// <summary>DPI of the monitor that hosts the window.</summary>
    public DpiScale Dpi { get; init; } = DpiScale.Default;

    /// <summary>Bounds of the monitor that hosts the window (physical pixels).</summary>
    public PixelRect MonitorBounds { get; init; }

    /// <summary>True when overlays for this window can currently be shown.</summary>
    public bool IsPresentable => IsVisible && !IsMinimized && !Bounds.IsEmpty;

    /// <inheritdoc />
    public override string ToString() => $"0x{Handle:X} '{Title}' ({ProcessName}:{ProcessId}) {Bounds}";
}
