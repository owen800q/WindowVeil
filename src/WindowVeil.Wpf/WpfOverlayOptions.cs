using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace WindowVeil.Wpf;

/// <summary>How <see cref="WpfOverlayRenderer"/> maps overlays to host windows.</summary>
public enum WpfHostingMode
{
    /// <summary>
    /// One layered host window (HWND) per overlay (default). Every overlay has its own z-order position, input mode and
    /// focus policy, and moving one overlay never repaints another. Costs one HWND and one layered surface per overlay.
    /// </summary>
    PerOverlay,

    /// <summary>
    /// Overlays that share a target window, input mode, focus policy and z-order strategy share one host window whose
    /// bounds are the union of its visible members; each overlay is a positioned child of a <c>Canvas</c>.
    /// </summary>
    /// <remarks>
    /// <para>Trade-offs compared with <see cref="PerOverlay"/>:</para>
    /// <list type="bullet">
    /// <item>Far fewer HWNDs and layered surfaces when a target carries many small components (badges, buttons, labels).</item>
    /// <item>Pass-through and interactive overlays are kept in separate hosts: the pass-through host is
    /// <c>WS_EX_TRANSPARENT</c>, the interactive host is not but has no background, and because layered windows hit-test
    /// per pixel, fully transparent gaps between interactive components still pass clicks to the target.</item>
    /// <item>All members share one z-order position, so they cannot be interleaved with other windows individually, and
    /// the shared surface spans the union of the members (two components at opposite corners of a large target produce a
    /// large, mostly transparent layered surface that is re-composited whenever any member changes).</item>
    /// <item>The host window is created lazily on the first update that names a target window, so
    /// <see cref="Rendering.IOverlayVisual.Handle"/> is 0 until then.</item>
    /// </list>
    /// </remarks>
    Grouped,
}

/// <summary>Options for <see cref="WpfOverlayRenderer"/>.</summary>
public sealed record WpfOverlayOptions
{
    /// <summary>
    /// An existing dispatcher to host overlays on, typically <c>Application.Current.Dispatcher</c> in a WPF application.
    /// When null (default) the renderer starts its own <see cref="WpfDispatcherHost"/> (a dedicated STA thread) on first use
    /// and shuts it down when disposed. A supplied dispatcher is never shut down by the renderer.
    /// </summary>
    /// <remarks>
    /// Use the application dispatcher when overlay content needs application-level resources (implicit styles, themes in
    /// <c>Application.Resources</c>): those resources belong to the application's thread and cannot be used safely from a
    /// dedicated overlay thread. A dedicated thread, on the other hand, keeps overlays responsive while the application's UI
    /// thread is busy.
    /// </remarks>
    public Dispatcher? Dispatcher { get; init; }

    /// <summary>Host window strategy. Default <see cref="WpfHostingMode.PerOverlay"/>.</summary>
    public WpfHostingMode HostingMode { get; init; } = WpfHostingMode.PerOverlay;

    /// <summary>Logger for renderer diagnostics and overlay content exceptions.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>Name of the dedicated dispatcher thread (when <see cref="Dispatcher"/> is null).</summary>
    public string ThreadName { get; init; } = "WindowVeil WPF overlays";
}
