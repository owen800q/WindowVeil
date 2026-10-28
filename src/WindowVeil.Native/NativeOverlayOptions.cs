namespace WindowVeil.Native;

/// <summary>Options for <see cref="NativeOverlayRenderer"/>.</summary>
public sealed class NativeOverlayOptions
{
    /// <summary>
    /// When true, overlay windows are marked with <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> (Windows 10
    /// 2004 and later). They then stay visible on the physical screen but are omitted from <b>every</b> capture:
    /// screenshots, screen recordings, screen sharing, and WindowVeil's own desktop capture. Use it when the overlay must
    /// never appear in captured material; leave it off (default) when screenshots should show the overlays.
    /// Failure to apply the affinity (older Windows) is logged, not thrown.
    /// </summary>
    public bool ExcludeFromCapture { get; init; }

    /// <summary>
    /// Message thread that owns the overlay windows. When null the renderer creates (and disposes) its own thread.
    /// A supplied thread is shared and is <b>not</b> disposed by the renderer.
    /// </summary>
    public NativeMessageThread? MessageThread { get; init; }
}
