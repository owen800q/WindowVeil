using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Native.Interop;
using WindowVeil.Rendering;
using static WindowVeil.Native.Interop.NativeMethods;

namespace WindowVeil.Native;

/// <summary>
/// Draws masks, highlights and spotlights as Win32 layered popup windows owned by a <see cref="NativeMessageThread"/>.
/// One window per visual; windows are created once and then only moved, resized, shown or hidden.
/// </summary>
/// <remarks>
/// <para>Window styles and why:</para>
/// <list type="bullet">
/// <item><c>WS_POPUP</c>: no caption, border or system menu; the window rectangle equals the covered area.</item>
/// <item><c>WS_EX_LAYERED</c>: enables per-window opacity (<c>SetLayeredWindowAttributes(LWA_ALPHA)</c>) and, combined
/// with <c>WS_EX_TRANSPARENT</c>, mouse click-through.</item>
/// <item><c>WS_EX_TOOLWINDOW</c>: never shown in the taskbar or Alt+Tab.</item>
/// <item><c>WS_EX_NOACTIVATE</c>: never becomes the foreground window, so the target keeps keyboard focus (omitted
/// only for interactive overlays with <see cref="OverlayFocusPolicy.ActivateOnClick"/>).</item>
/// <item><c>WS_EX_TRANSPARENT</c>: only for pass-through overlays (and always for highlights and spotlights).</item>
/// <item><c>WS_EX_TOPMOST</c>: only for <see cref="OverlayZOrder.Topmost"/>.</item>
/// </list>
/// <para>
/// Overlays never use owner/parent relationships with the target: a cross-process owner or parent implicitly attaches
/// the two threads' input queues (<c>AttachThreadInput</c>), which lets a hung target freeze WindowVeil and vice versa.
/// Instead, z-order is maintained explicitly on every update.
/// </para>
/// </remarks>
public sealed class NativeOverlayRenderer : IOverlayRenderer
{
    /// <summary>Window class name of native overlay windows.</summary>
    public const string WindowClassName = "WindowVeil.NativeOverlay";

    /// <summary>All native overlay windows of the process (any renderer), keyed by HWND, for the shared window procedure.</summary>
    private static readonly ConcurrentDictionary<nint, NativeOverlayVisual> HandleMap = new();

    private readonly NativeMessageThread _thread;
    private readonly bool _ownsThread;
    private readonly NativeOverlayOptions _options;
    private readonly ILogger _logger;
    private readonly HashSet<NativeOverlayVisual> _live = [];
    private readonly Dictionary<uint, (nint Brush, int Refs)> _brushes = []; // message thread only
    private bool _classAcquired; // message thread only
    private int _created;
    private int _liveCount;
    private int _disposeState;

    /// <summary>Creates a renderer.</summary>
    /// <param name="options">Options; when <see cref="NativeOverlayOptions.MessageThread"/> is null a private thread is created.</param>
    /// <param name="logger">Logger.</param>
    public NativeOverlayRenderer(NativeOverlayOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new NativeOverlayOptions();
        _logger = logger ?? NullLogger.Instance;
        if (_options.MessageThread is { } shared)
        {
            ObjectDisposedException.ThrowIf(shared.IsDisposed, shared);
            _thread = shared;
        }
        else
        {
            _thread = new NativeMessageThread("WindowVeil native overlays");
            _ownsThread = true;
        }
    }

    /// <inheritdoc />
    public string Name => "Native";

    /// <summary>The thread that owns every overlay window of this renderer.</summary>
    public NativeMessageThread MessageThread => _thread;

    /// <summary>Total number of overlay windows created by this renderer (diagnostics and leak tests).</summary>
    public int CreatedWindowCount => Volatile.Read(ref _created);

    /// <summary>Number of overlay windows of this renderer that currently exist.</summary>
    public int LiveWindowCount => Volatile.Read(ref _liveCount);

    /// <summary>Options in use.</summary>
    public NativeOverlayOptions Options => _options;

    /// <inheritdoc />
    public bool CanRender(OverlayAppearance appearance) => appearance is MaskAppearance or HighlightAppearance or SpotlightAppearance;

    /// <inheritdoc />
    public async ValueTask<IOverlayVisual> CreateVisualAsync(OverlayVisualRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Appearance);
        if (!CanRender(request.Appearance))
            throw new WindowVeilException(WindowVeilErrorCode.RendererNotFound, $"The native renderer cannot draw '{request.Appearance.Kind}' appearances.");
        ThrowIfDisposed();
        return await _thread.InvokeAsync(() => CreateOnThread(request), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(IReadOnlyList<OverlayVisualUpdate> updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return;
        var items = new (NativeOverlayVisual Visual, OverlayFrame Frame)[updates.Count];
        for (int i = 0; i < updates.Count; i++)
        {
            var u = updates[i];
            if (u.Visual is not NativeOverlayVisual v || !ReferenceEquals(v.Renderer, this))
                throw new ArgumentException("The update refers to a visual that was not created by this renderer.", nameof(updates));
            items[i] = (v, u.Frame ?? throw new ArgumentException("An update has no frame.", nameof(updates)));
        }
        ThrowIfDisposed();
        await _thread.InvokeAsync(() => ApplyOnThread(items), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Destroys every remaining overlay window, unregisters the window class and stops the thread if owned.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        try
        {
            await _thread.InvokeAsync(() =>
            {
                NativeOverlayVisual[] remaining;
                lock (_live) remaining = [.. _live];
                foreach (var v in remaining) DestroyOnThread(v);
                ReleaseClassAndBrushes();
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The (shared) thread is already gone; the system destroyed its windows when it exited. Release what
            // the process still holds: registry entries and GDI brushes.
            NativeOverlayVisual[] remaining;
            lock (_live) remaining = [.. _live];
            foreach (var v in remaining) ReleaseVisualResources(v);
            ReleaseClassAndBrushes();
        }
        if (_ownsThread) _thread.Dispose();
    }

    // ------------------------------------------------------------------ creation & destruction (message thread)

    private unsafe NativeOverlayVisual CreateOnThread(OverlayVisualRequest request)
    {
        ThrowIfDisposed();
        if (!_classAcquired)
        {
            delegate* unmanaged<nint, uint, nint, nint, nint> proc = &OverlayWindowProc;
            try { WindowClassRegistry.Acquire(WindowClassName, (nint)proc, LoadCursor(0, IDC_ARROW)); }
            catch (Win32Exception ex)
            {
                throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed,
                    $"Could not register the overlay window class: {ex.Message} (error {ex.NativeErrorCode}).", ex);
            }
            _classAcquired = true;
        }

        // Highlights and spotlights are purely visual. Masks honor the requested input mode.
        bool clickThrough = request.Appearance is not MaskAppearance || request.InputMode == OverlayInputMode.PassThrough;
        uint exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        if (clickThrough || request.FocusPolicy == OverlayFocusPolicy.NoActivate) exStyle |= WS_EX_NOACTIVATE;
        if (clickThrough) exStyle |= WS_EX_TRANSPARENT; // layered + transparent = hit-testing skips the window
        if (request.ZOrder == OverlayZOrder.Topmost) exStyle |= WS_EX_TOPMOST;

        // Created hidden (no WS_VISIBLE) at 1x1; the first update positions and shows it without activation.
        nint hwnd = CreateWindowEx(exStyle, WindowClassName, "WindowVeil Overlay", WS_POPUP, 0, 0, 1, 1, 0, 0, WindowClassRegistry.ModuleHandle, 0);
        if (hwnd == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed,
                $"CreateWindowEx failed for rule '{request.RuleId}': {new Win32Exception(error).Message} (error {error}).");
        }

        var visual = new NativeOverlayVisual(this, request, hwnd, clickThrough);
        try
        {
            HandleMap[hwnd] = visual;
            lock (_live) _live.Add(visual);
            Interlocked.Increment(ref _liveCount);
            Interlocked.Increment(ref _created);
            OverlayWindowRegistry.Register(hwnd);
            ApplyAppearanceOnThread(visual, request.Appearance, initial: true);
            if (_options.ExcludeFromCapture && !SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE))
            {
                _logger.LogWarning("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed with error {Error}; the overlay will appear in captures.",
                    Marshal.GetLastPInvokeError());
            }
        }
        catch
        {
            DestroyOnThread(visual); // never leave an orphaned window behind
            throw;
        }
        _logger.LogDebug("Created native overlay 0x{Handle:X} for rule {RuleId}", hwnd, request.RuleId);
        return visual;
    }

    internal async ValueTask DestroyVisualAsync(NativeOverlayVisual visual)
    {
        if (visual.IsDestroyed) return;
        try
        {
            await _thread.InvokeAsync(() => DestroyOnThread(visual)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Thread gone: the window died with it. Release the process-side bookkeeping.
            ReleaseVisualResources(visual);
        }
    }

    private void DestroyOnThread(NativeOverlayVisual visual)
    {
        if (visual.IsDestroyed) return;
        if (IsWindow(visual.Handle) && !DestroyWindow(visual.Handle))
            _logger.LogWarning("DestroyWindow(0x{Handle:X}) failed with error {Error}", visual.Handle, Marshal.GetLastPInvokeError());
        // WM_NCDESTROY normally released everything already; this is idempotent.
        ReleaseVisualResources(visual);
    }

    /// <summary>Idempotent bookkeeping cleanup; safe on any thread once the window is gone.</summary>
    private void ReleaseVisualResources(NativeOverlayVisual visual)
    {
        if (!visual.MarkDestroyed()) return;
        HandleMap.TryRemove(new KeyValuePair<nint, NativeOverlayVisual>(visual.Handle, visual));
        OverlayWindowRegistry.Unregister(visual.Handle);
        bool removed;
        lock (_live) removed = _live.Remove(visual);
        if (removed) Interlocked.Decrement(ref _liveCount);
        if (visual.BrushColor is { } color && (_thread.CheckAccess() || _thread.IsDisposed)) ReleaseBrush(color);
        visual.BrushColor = null;
        visual.Brush = 0;
    }

    private void ReleaseClassAndBrushes()
    {
        foreach (var (brush, _) in _brushes.Values) DeleteObject(brush);
        _brushes.Clear();
        if (_classAcquired)
        {
            _classAcquired = false;
            WindowClassRegistry.Release(WindowClassName);
        }
    }

    // ------------------------------------------------------------------ appearance (message thread)

    internal Task SetAppearanceAsync(NativeOverlayVisual visual, OverlayAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (appearance.GetType() != visual.Appearance.GetType())
            throw new ArgumentException($"A {visual.Appearance.GetType().Name} visual cannot switch to {appearance.GetType().Name}; create a new visual instead.", nameof(appearance));
        ThrowIfDisposed();
        return _thread.InvokeAsync(() =>
        {
            if (visual.IsDestroyed) throw new ObjectDisposedException(nameof(NativeOverlayVisual));
            ApplyAppearanceOnThread(visual, appearance, initial: false);
            // Geometry can depend on the appearance (highlight thickness).
            if (visual.CurrentFrame is { } frame) ApplyOnThread([(visual, frame)]);
        });
    }

    private void ApplyAppearanceOnThread(NativeOverlayVisual visual, OverlayAppearance appearance, bool initial)
    {
        var (color, opacity) = appearance switch
        {
            MaskAppearance m => (m.Color, m.Opacity),
            HighlightAppearance h => (h.Color, h.Opacity),
            SpotlightAppearance s => (s.DimColor, s.Opacity),
            _ => throw new ArgumentException($"'{appearance.Kind}' appearances are not rendered natively.", nameof(appearance)),
        };
        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 255, MidpointRounding.AwayFromZero), 0, 255);

        // A layered window stays invisible until SetLayeredWindowAttributes (or UpdateLayeredWindow) is called once.
        if (initial || alpha != visual.Alpha)
        {
            if (!SetLayeredWindowAttributes(visual.Handle, 0, alpha, LWA_ALPHA))
            {
                int error = Marshal.GetLastPInvokeError();
                throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed,
                    $"SetLayeredWindowAttributes failed: {new Win32Exception(error).Message} (error {error}).");
            }
            visual.Alpha = alpha;
        }

        uint colorRef = ToColorRef(color);
        if (visual.BrushColor != colorRef)
        {
            nint brush = AcquireBrush(colorRef);
            if (visual.BrushColor is { } old) ReleaseBrush(old);
            visual.Brush = brush;
            visual.BrushColor = colorRef;
            if (!initial) unsafe { InvalidateRect(visual.Handle, null, true); }
        }
        visual.Appearance = appearance;
    }

    private nint AcquireBrush(uint colorRef)
    {
        if (_brushes.TryGetValue(colorRef, out var entry))
        {
            _brushes[colorRef] = (entry.Brush, entry.Refs + 1);
            return entry.Brush;
        }
        nint brush = CreateSolidBrush(colorRef);
        if (brush == 0) throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, "CreateSolidBrush failed (GDI object quota exhausted?).");
        _brushes[colorRef] = (brush, 1);
        return brush;
    }

    private void ReleaseBrush(uint colorRef)
    {
        if (!_brushes.TryGetValue(colorRef, out var entry)) return;
        if (entry.Refs <= 1)
        {
            _brushes.Remove(colorRef);
            DeleteObject(entry.Brush);
        }
        else
        {
            _brushes[colorRef] = (entry.Brush, entry.Refs - 1);
        }
    }

    // ------------------------------------------------------------------ updates (message thread)

    private readonly record struct PositionOp(NativeOverlayVisual Visual, nint InsertAfter, PixelRect Bounds, uint Flags, bool VisibleAfter);

    private void ApplyOnThread((NativeOverlayVisual Visual, OverlayFrame Frame)[] items)
    {
        var ops = new List<PositionOp>(items.Length);
        List<string>? errors = null;
        foreach (var (visual, frame) in items)
        {
            if (visual.IsDestroyed) continue;
            try
            {
                var layout = OverlayLayout.Compute(visual.Appearance, frame);
                if (Prepare(visual, layout, frame) is { } op) ops.Add(op);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Failed to prepare overlay 0x{Handle:X}", visual.Handle);
                (errors ??= []).Add($"0x{visual.Handle:X}: {ex.Message}");
            }
        }

        foreach (var error in ExecutePositionOps(ops)) (errors ??= []).Add(error);
        foreach (var (visual, frame) in items) visual.CurrentFrame = frame;
        if (errors is not null)
            throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, "Failed to update native overlays: " + string.Join("; ", errors));
    }

    private PositionOp? Prepare(NativeOverlayVisual visual, OverlayLayout layout, OverlayFrame frame)
    {
        const uint common = SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        if (!layout.Visible)
        {
            if (!visual.AppliedVisible) return null; // already hidden: no-op
            return new PositionOp(visual, 0, visual.AppliedBounds,
                common | SWP_HIDEWINDOW | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER, VisibleAfter: false);
        }

        if (!OverlayLayout.SameShapes(layout.Shapes, visual.AppliedShapes)) ApplyRegion(visual, layout.Shapes);

        bool moveSize = layout.Bounds != visual.AppliedBounds;
        bool show = !visual.AppliedVisible;
        bool zChange = ComputeInsertAfter(visual, frame.TargetWindow, show, out nint insertAfter);
        if (!moveSize && !show && !zChange) return null;

        uint flags = common;
        if (!moveSize) flags |= SWP_NOMOVE | SWP_NOSIZE;
        if (!zChange) flags |= SWP_NOZORDER;
        if (show) flags |= SWP_SHOWWINDOW;
        return new PositionOp(visual, insertAfter, layout.Bounds, flags, VisibleAfter: true);
    }

    /// <summary>
    /// Decides where the overlay goes in the z-order. Returns false when it is already correctly placed.
    /// </summary>
    /// <remarks>
    /// <see cref="OverlayZOrder.AboveTarget"/>: SetWindowPos places a window <i>behind</i> hWndInsertAfter, so the
    /// overlay is inserted behind the first non-overlay window above the target. WindowVeil overlays directly above the
    /// target (a "run") are skipped, so several overlays of one target stack together above it; when this overlay is
    /// already part of that run nothing changes. A topmost window above a non-topmost target, or no window at all,
    /// means "top of the non-topmost band" (<c>HWND_TOP</c>/<c>HWND_NOTOPMOST</c>). Topmost targets get topmost overlays.
    /// </remarks>
    private static bool ComputeInsertAfter(NativeOverlayVisual visual, nint target, bool showing, out nint insertAfter)
    {
        insertAfter = HWND_TOP;
        if (visual.ZOrder == OverlayZOrder.Topmost)
        {
            insertAfter = HWND_TOPMOST; // re-asserted each update: other topmost windows may have been raised meanwhile
            return true;
        }
        if (target == 0 || !IsWindow(target)) return showing;

        bool overlayTopmost = IsTopmost(visual.Handle);
        bool targetTopmost = IsTopmost(target);
        nint above = NativeMethods.GetWindow(target, GW_HWNDPREV);
        for (int guard = 0; above != 0 && guard < 512 && OverlayWindowRegistry.IsOverlay(above); guard++)
        {
            if (above == visual.Handle) return overlayTopmost != targetTopmost && FixBand(targetTopmost, out insertAfter);
            above = NativeMethods.GetWindow(above, GW_HWNDPREV);
        }

        if (targetTopmost) { insertAfter = HWND_TOPMOST; return true; }
        if (above == 0 || IsTopmost(above))
        {
            insertAfter = overlayTopmost ? HWND_NOTOPMOST : HWND_TOP;
            return true;
        }
        // If the overlay is currently topmost, inserting it after a non-topmost window also clears its topmost state.
        insertAfter = above;
        return true;
    }

    private static bool FixBand(bool targetTopmost, out nint insertAfter)
    {
        insertAfter = targetTopmost ? HWND_TOPMOST : HWND_NOTOPMOST;
        return true;
    }

    private static bool IsTopmost(nint hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & (nint)WS_EX_TOPMOST) != 0;

    /// <summary>Applies all moves/sizes/visibility changes in one DeferWindowPos batch (one compositor update).</summary>
    private List<string> ExecutePositionOps(List<PositionOp> ops)
    {
        var errors = new List<string>();
        if (ops.Count == 0) return errors;

        bool batched = false;
        if (ops.Count > 1)
        {
            nint hdwp = BeginDeferWindowPos(ops.Count);
            foreach (var op in ops)
            {
                if (hdwp == 0) break;
                hdwp = DeferWindowPos(hdwp, op.Visual.Handle, op.InsertAfter, op.Bounds.X, op.Bounds.Y, op.Bounds.Width, op.Bounds.Height, op.Flags);
            }
            // When DeferWindowPos fails the batch is already freed and EndDeferWindowPos must not be called.
            if (hdwp != 0)
            {
                batched = EndDeferWindowPos(hdwp);
                if (!batched) _logger.LogDebug("EndDeferWindowPos failed with error {Error}; falling back to SetWindowPos", Marshal.GetLastPInvokeError());
            }
            else
            {
                _logger.LogDebug("DeferWindowPos failed with error {Error}; falling back to SetWindowPos", Marshal.GetLastPInvokeError());
            }
        }

        foreach (var op in ops)
        {
            bool ok = batched;
            if (!ok)
            {
                ok = SetWindowPos(op.Visual.Handle, op.InsertAfter, op.Bounds.X, op.Bounds.Y, op.Bounds.Width, op.Bounds.Height, op.Flags);
                if (!ok && (op.Flags & SWP_NOZORDER) == 0)
                {
                    // The window we wanted to insert after may have been destroyed meanwhile: keep the current z-order.
                    ok = SetWindowPos(op.Visual.Handle, 0, op.Bounds.X, op.Bounds.Y, op.Bounds.Width, op.Bounds.Height, op.Flags | SWP_NOZORDER);
                }
                if (!ok)
                {
                    int error = Marshal.GetLastPInvokeError();
                    errors.Add($"SetWindowPos(0x{op.Visual.Handle:X}) failed: {new Win32Exception(error).Message} (error {error})");
                    continue;
                }
            }
            op.Visual.AppliedVisible = op.VisibleAfter;
            if ((op.Flags & SWP_NOSIZE) == 0) op.Visual.AppliedBounds = op.Bounds;
        }
        return errors;
    }

    private void ApplyRegion(NativeOverlayVisual visual, IReadOnlyList<OverlayRegionShape>? shapes)
    {
        nint region = shapes is null ? 0 : CreateRegion(shapes);
        // While hidden there is nothing to repaint.
        if (SetWindowRgn(visual.Handle, region, visual.AppliedVisible) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (region != 0) DeleteObject(region); // ownership is only transferred on success
            throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, $"SetWindowRgn failed: {new Win32Exception(error).Message} (error {error}).");
        }
        // On success the system owns the region: it must not be deleted here.
        visual.AppliedShapes = shapes;
    }

    /// <summary>Builds union over shapes of (outer minus holes). Temporary regions are always deleted.</summary>
    internal static nint CreateRegion(IReadOnlyList<OverlayRegionShape> shapes)
    {
        nint combined = CreateRectRgn(0, 0, 0, 0);
        if (combined == 0) throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, "CreateRectRgn failed.");
        try
        {
            foreach (var shape in shapes)
            {
                nint piece = CreateRect(shape.Outer);
                try
                {
                    foreach (var hole in shape.Holes)
                    {
                        nint h = CreateRect(hole);
                        try { if (CombineRgn(piece, piece, h, RGN_DIFF) == NativeMethods.ERROR) throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, "CombineRgn(RGN_DIFF) failed."); }
                        finally { DeleteObject(h); }
                    }
                    if (CombineRgn(combined, combined, piece, RGN_OR) == NativeMethods.ERROR)
                        throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, "CombineRgn(RGN_OR) failed.");
                }
                finally { DeleteObject(piece); }
            }
            return combined;
        }
        catch
        {
            DeleteObject(combined);
            throw;
        }

        static nint CreateRect(PixelRect r)
        {
            nint rgn = CreateRectRgn(r.X, r.Y, r.Right, r.Bottom);
            return rgn != 0 ? rgn : throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, "CreateRectRgn failed.");
        }
    }

    // ------------------------------------------------------------------ window procedure

    /// <summary>
    /// Shared window procedure of every native overlay. A static <c>[UnmanagedCallersOnly]</c> method: its function
    /// pointer is permanently valid, so there is no delegate that could be garbage collected while USER32 still calls it.
    /// Exceptions are caught and logged; they must never unwind into native code.
    /// </summary>
    [UnmanagedCallersOnly]
    private static unsafe nint OverlayWindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    // Defense in depth: WS_EX_LAYERED|WS_EX_TRANSPARENT already excludes the window from hit-testing.
                    if (HandleMap.TryGetValue(hwnd, out var hv) && hv.IsClickThrough) return HTTRANSPARENT;
                    break;
                case WM_MOUSEACTIVATE:
                    return HandleMap.TryGetValue(hwnd, out var av) && !av.IsClickThrough && av.FocusPolicy == OverlayFocusPolicy.ActivateOnClick
                        ? MA_ACTIVATE
                        : MA_NOACTIVATE; // the click is consumed by the overlay but the target keeps focus
                case WM_ERASEBKGND:
                    return 1; // everything is painted in WM_PAINT; avoids flicker
                case WM_PAINT:
                    Paint(hwnd);
                    return 0;
                case WM_GETMINMAXINFO:
                    // Popups get default min/max tracking sizes; overlays must take exactly the requested size
                    // (thin highlight borders, masks larger than one monitor).
                    var mmi = (MINMAXINFO*)lParam;
                    mmi->ptMinTrackSize = new POINT(1, 1);
                    mmi->ptMaxTrackSize = new POINT(short.MaxValue, short.MaxValue);
                    return 0;
                case WM_DPICHANGED:
                    return 0; // geometry is always set explicitly in physical pixels; never auto-resize
                case WM_CLOSE:
                    return 0; // only the renderer destroys overlays
                case WM_NCDESTROY:
                    if (HandleMap.TryGetValue(hwnd, out var dv)) dv.Renderer.ReleaseVisualResources(dv);
                    break;
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (HandleMap.TryGetValue(hwnd, out var v)) v.Renderer._logger.LogError(ex, "Overlay window procedure failed (message 0x{Message:X})", msg);
                else Trace.TraceError("WindowVeil overlay window procedure failed: {0}", ex);
            }
            catch { /* never throw into user32 */ }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void Paint(nint hwnd)
    {
        nint hdc = BeginPaint(hwnd, out PAINTSTRUCT ps);
        if (hdc == 0) return;
        try
        {
            if (HandleMap.TryGetValue(hwnd, out var v) && v.Brush != 0) FillRect(hdc, in ps.rcPaint, v.Brush);
        }
        finally { EndPaint(hwnd, in ps); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}

/// <summary>A native overlay window created by <see cref="NativeOverlayRenderer"/>.</summary>
public sealed class NativeOverlayVisual : IOverlayVisual
{
    private volatile OverlayFrame? _currentFrame;
    private volatile OverlayAppearance _appearance;
    private int _destroyed;

    internal NativeOverlayVisual(NativeOverlayRenderer renderer, OverlayVisualRequest request, nint handle, bool clickThrough)
    {
        Renderer = renderer;
        RuleId = request.RuleId;
        Handle = handle;
        _appearance = request.Appearance;
        InputMode = request.InputMode;
        FocusPolicy = request.FocusPolicy;
        ZOrder = request.ZOrder;
        IsClickThrough = clickThrough;
    }

    internal NativeOverlayRenderer Renderer { get; }

    /// <inheritdoc />
    public string RuleId { get; }

    /// <summary>The overlay window handle (HWND). Owned by the renderer's message thread.</summary>
    public nint Handle { get; }

    /// <inheritdoc />
    public OverlayFrame? CurrentFrame { get => _currentFrame; internal set => _currentFrame = value; }

    /// <summary>Current appearance.</summary>
    public OverlayAppearance Appearance { get => _appearance; internal set => _appearance = value; }

    /// <summary>Requested input mode.</summary>
    public OverlayInputMode InputMode { get; }

    /// <summary>Requested focus policy.</summary>
    public OverlayFocusPolicy FocusPolicy { get; }

    /// <summary>Z-order strategy.</summary>
    public OverlayZOrder ZOrder { get; }

    /// <summary>Whether mouse input passes through to the windows below.</summary>
    public bool IsClickThrough { get; }

    /// <summary>Whether the window was destroyed.</summary>
    public bool IsDestroyed => Volatile.Read(ref _destroyed) != 0;

    // Message-thread state.
    internal byte Alpha;
    internal nint Brush;
    internal uint? BrushColor;
    internal bool AppliedVisible;
    internal PixelRect AppliedBounds = new(0, 0, 1, 1);
    internal IReadOnlyList<OverlayRegionShape>? AppliedShapes;

    internal bool MarkDestroyed() => Interlocked.Exchange(ref _destroyed, 1) == 0;

    /// <summary>Native visuals have no intrinsic content size.</summary>
    public ValueTask<PixelSize?> MeasureAsync(DpiScale dpi, CancellationToken cancellationToken = default) => ValueTask.FromResult<PixelSize?>(null);

    /// <summary>
    /// Changes color/opacity (and highlight thickness) without recreating the window. The new appearance must be of the
    /// same type (mask, highlight or spotlight) because click-through behavior is fixed at creation.
    /// </summary>
    /// <exception cref="ArgumentException">The appearance type differs.</exception>
    public Task SetAppearanceAsync(OverlayAppearance appearance) => Renderer.SetAppearanceAsync(this, appearance);

    /// <summary>Destroys the window on the message thread. Safe to call several times.</summary>
    public ValueTask DisposeAsync() => Renderer.DestroyVisualAsync(this);

    /// <inheritdoc />
    public override string ToString() => $"NativeOverlay 0x{Handle:X} rule={RuleId} {Appearance.Kind}";
}
