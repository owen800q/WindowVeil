using WindowVeil.Geometry;

namespace WindowVeil.Platform;

/// <summary>
/// Access to top-level windows of the desktop. Implemented by <c>WindowVeil.Native</c> on Windows;
/// tests use in-memory fakes. Implementations must be thread-safe.
/// </summary>
public interface IWindowSystem
{
    /// <summary>Enumerates top-level windows (including invisible ones; filter with <see cref="WindowSelector"/>).</summary>
    IReadOnlyList<TargetWindowInfo> EnumerateWindows();

    /// <summary>Returns a fresh snapshot of a window, or null when the handle no longer identifies a window.</summary>
    TargetWindowInfo? GetWindow(nint handle);

    /// <summary>Returns true when the process is still running.</summary>
    bool IsProcessAlive(int processId);

    /// <summary>Bounds of the virtual desktop (union of all monitors) in physical pixels.</summary>
    PixelRect VirtualScreenBounds { get; }
}
