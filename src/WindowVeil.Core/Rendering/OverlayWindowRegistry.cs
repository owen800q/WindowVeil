using System.Collections.Concurrent;

namespace WindowVeil.Rendering;

/// <summary>
/// Process-wide set of WindowVeil overlay window handles. Renderers register their host windows so that tracking,
/// UI Automation and pickers can ignore WindowVeil's own windows.
/// </summary>
public static class OverlayWindowRegistry
{
    private static readonly ConcurrentDictionary<nint, byte> Handles = new();

    /// <summary>Registers an overlay window.</summary>
    public static void Register(nint handle) { if (handle != 0) Handles[handle] = 0; }

    /// <summary>Unregisters an overlay window.</summary>
    public static void Unregister(nint handle) => Handles.TryRemove(handle, out _);

    /// <summary>Whether the handle belongs to a WindowVeil overlay.</summary>
    public static bool IsOverlay(nint handle) => Handles.ContainsKey(handle);

    /// <summary>Snapshot of all registered handles.</summary>
    public static IReadOnlySet<nint> Snapshot() => new HashSet<nint>(Handles.Keys);

    /// <summary>Number of live overlay windows (useful for leak tests).</summary>
    public static int Count => Handles.Count;
}
