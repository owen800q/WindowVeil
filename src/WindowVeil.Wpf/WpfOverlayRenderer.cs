using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Appearance;
using WindowVeil.Geometry;
using DpiScale = WindowVeil.Geometry.DpiScale;
using WindowVeil.Rendering;
using WindowVeil.Wpf.Hosting;
using WindowVeil.Wpf.Interop;

namespace WindowVeil.Wpf;

/// <summary>
/// Renders <see cref="WpfAppearance"/> overlays: arbitrary WPF content hosted in layered, borderless, non-activating
/// top-level windows positioned over external application windows. WPF draws the content with its own DirectX-based
/// composition pipeline; WindowVeil only creates, positions and destroys the host windows. Nothing is injected into, or
/// hooked in, the target process.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> Every WPF object is created and used on one dispatcher thread: either a dedicated
/// <see cref="WpfDispatcherHost"/> started on first use, or <see cref="WpfOverlayOptions.Dispatcher"/>. All methods may be
/// called from any thread; each call marshals once to the dispatcher (an <see cref="ApplyAsync"/> batch is a single
/// dispatcher invocation with a single <c>DeferWindowPos</c> transaction).</para>
/// <para><b>Input.</b> <see cref="OverlayInputMode.PassThrough"/> hosts are <c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>: they
/// are invisible to hit-testing and every click reaches the application underneath.
/// <see cref="OverlayInputMode.Interactive"/> hosts receive mouse input (clicks are not forwarded to the target), except on
/// fully transparent pixels, which layered windows never hit-test.</para>
/// <para><b>Focus.</b> With <see cref="OverlayFocusPolicy.NoActivate"/> (default) hosts are <c>WS_EX_NOACTIVATE</c> and
/// answer <c>WM_MOUSEACTIVATE</c> with <c>MA_NOACTIVATE</c>: buttons and commands work, but the target keeps activation and
/// keyboard focus. <see cref="OverlayFocusPolicy.ActivateOnClick"/> lets the overlay activate when clicked so text boxes can
/// receive keyboard input.</para>
/// <para><b>Placement.</b> Bounds are applied in physical pixels with <c>SetWindowPos</c>/<c>DeferWindowPos</c> (never through
/// WPF's DIP-based <c>Left</c>/<c>Top</c>, which depend on the monitor the window currently occupies), and the content root
/// is sized to <c>physical / scale</c> DIPs. <see cref="OverlayZOrder.AboveTarget"/> inserts the host directly above the
/// target window; <see cref="OverlayZOrder.Topmost"/> uses <c>HWND_TOPMOST</c>. Hosts are never owned by the target
/// (cross-process ownership is not used).</para>
/// <para><b>Hosting modes.</b> See <see cref="WpfHostingMode"/> for the per-overlay and grouped strategies.</para>
/// </remarks>
public sealed class WpfOverlayRenderer : IOverlayRenderer
{
    private readonly WpfOverlayOptions _options;
    private readonly ILogger _logger;
    private readonly object _startLock = new();
    private readonly ConcurrentDictionary<nint, byte> _handles = new();
    private WpfDispatcherHost? _ownedHost;
    private Dispatcher? _dispatcher;
    private int _disposed;

    // Dispatcher-thread state.
    private readonly HashSet<WpfOverlayVisual> _visuals = [];
    private readonly Dictionary<OverlayGroupKey, OverlayGroup> _groups = [];

    /// <summary>Creates a renderer. The dedicated dispatcher thread (if any) starts lazily on first use.</summary>
    public WpfOverlayRenderer(WpfOverlayOptions? options = null)
    {
        _options = options ?? new WpfOverlayOptions();
        _logger = _options.Logger ?? NullLogger.Instance;
        _dispatcher = _options.Dispatcher;
    }

    /// <inheritdoc />
    public string Name => "WPF";

    /// <summary>The hosting strategy.</summary>
    public WpfHostingMode HostingMode => _options.HostingMode;

    /// <summary>
    /// The dispatcher that owns every overlay element and host window. Accessing it starts the dedicated dispatcher thread
    /// when no dispatcher was supplied in the options.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The renderer was disposed.</exception>
    public Dispatcher Dispatcher => GetDispatcher();

    /// <summary>Whether the renderer owns its dispatcher thread (no <see cref="WpfOverlayOptions.Dispatcher"/> was supplied).</summary>
    public bool OwnsDispatcher => _options.Dispatcher is null;

    /// <inheritdoc />
    public bool CanRender(OverlayAppearance appearance) => appearance is WpfAppearance;

    // ------------------------------------------------------------------ create

    /// <inheritdoc />
    /// <exception cref="WindowVeilException">The factory threw or returned an unusable element
    /// (<see cref="WindowVeilErrorCode.OverlayCreationFailed"/>), or the dispatcher is unavailable
    /// (<see cref="WindowVeilErrorCode.WpfDispatcherFailure"/>).</exception>
    public async ValueTask<IOverlayVisual> CreateVisualAsync(OverlayVisualRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Appearance is not WpfAppearance appearance)
            throw new ArgumentException($"{nameof(WpfOverlayRenderer)} cannot render '{request.Appearance?.Kind}' appearances.", nameof(request));
        var dispatcher = GetDispatcher();
        return await DispatcherInvoke.RunAsync(dispatcher, () => CreateOnDispatcher(request, appearance, dispatcher), cancellationToken).ConfigureAwait(false);
    }

    private WpfOverlayVisual CreateOnDispatcher(OverlayVisualRequest request, WpfAppearance appearance, Dispatcher dispatcher)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var context = new WpfOverlayContext(request.RuleId, dispatcher, request);

        UIElement? content;
        try
        {
            content = appearance.Factory(context);
        }
        catch (Exception ex)
        {
            throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed,
                $"The WPF content factory of rule '{request.RuleId}' threw {ex.GetType().Name}: {ex.Message}", ex);
        }
        ValidateContent(request.RuleId, content, dispatcher);

        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = ToBrush(appearance.Background),
            Opacity = appearance.Opacity,
            ClipToBounds = true,
        };
        OverlayHostWindow? host = null;
        try
        {
            root.Children.Add(content!);
            var visual = new WpfOverlayVisual(this, request, appearance, context, content!, root);
            if (_options.HostingMode == WpfHostingMode.PerOverlay)
            {
                host = new OverlayHostWindow(request.InputMode, request.FocusPolicy, request.ZOrder, Brushes.Transparent) { Content = root };
                var created = host;
                host.Closed += (_, _) => Unregister(created.Handle);
                host.Relayout = scale => LayoutSingle(visual, scale);
                // Creates the HWND (and applies the extended styles in SourceInitialized) but keeps it hidden.
                var hwnd = host.EnsureCreated();
                Register(hwnd);
                visual.Host = host;
                visual.SetHandle(hwnd);
            }
            _visuals.Add(visual);
            return visual;
        }
        catch (Exception ex)
        {
            // Never leak a window when creation fails half-way.
            if (host is not null) CloseHost(host);
            root.Children.Clear();
            throw ex as WindowVeilException ?? new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed,
                $"Failed to create the WPF overlay host for rule '{request.RuleId}': {ex.Message}", ex);
        }
    }

    private static void ValidateContent(string ruleId, UIElement? content, Dispatcher dispatcher)
    {
        string? problem = content switch
        {
            null => "returned null",
            Window => "returned a Window; return the window's content (a UIElement) instead",
            _ when content.Dispatcher != dispatcher =>
                "returned an element created on another thread; the factory must create a new element each time it is called",
            _ when VisualTreeHelper.GetParent(content) is not null || LogicalTreeHelper.GetParent(content) is not null =>
                "returned an element that already has a parent; the factory must create a new element each time it is called",
            _ => null,
        };
        if (problem is not null)
            throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed, $"The WPF content factory of rule '{ruleId}' {problem}.");
    }

    // ------------------------------------------------------------------ measure

    internal async ValueTask<PixelSize?> MeasureAsync(WpfOverlayVisual visual, DpiScale dpi, CancellationToken cancellationToken)
    {
        if (visual.IsDisposed || !visual.Appearance.SizeToContent) return null;
        return await DispatcherInvoke.RunAsync(GetDispatcher(), () =>
        {
            if (visual.IsDisposed) return (PixelSize?)null;
            var content = visual.Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = content.DesiredSize;
            // The next layout pass must measure again with the real constraint.
            content.InvalidateMeasure();
            visual.Root.InvalidateMeasure();
            if (desired.Width <= 0 && desired.Height <= 0) return null;
            return new PixelSize(ToPhysical(desired.Width, dpi.Scale), ToPhysical(desired.Height, dpi.Scale));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static int ToPhysical(double dip, double scale)
    {
        // Round up so content is never clipped; the epsilon absorbs floating-point noise (140 * 1.5 = 210, not 211).
        var px = Math.Ceiling(dip * scale - 1e-6);
        return px <= 0 ? 0 : px >= int.MaxValue ? int.MaxValue : (int)px;
    }

    // ------------------------------------------------------------------ apply

    /// <inheritdoc />
    public async ValueTask ApplyAsync(IReadOnlyList<OverlayVisualUpdate> updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return;
        var batch = new List<(WpfOverlayVisual Visual, OverlayFrame Frame)>(updates.Count);
        foreach (var u in updates)
        {
            if (u.Visual is not WpfOverlayVisual v || !ReferenceEquals(v.Renderer, this))
                throw new ArgumentException("The update contains a visual that was not created by this renderer.", nameof(updates));
            ArgumentNullException.ThrowIfNull(u.Frame, nameof(updates));
            batch.Add((v, u.Frame));
        }
        var errors = await DispatcherInvoke.RunAsync(GetDispatcher(), () => ApplyOnDispatcher(batch), cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
            throw new WindowVeilException(WindowVeilErrorCode.OverlayCreationFailed,
                $"Failed to apply {errors.Count} WPF overlay update(s): {errors[0].Message}",
                errors.Count == 1 ? errors[0] : new AggregateException(errors));
    }

    private readonly record struct HostPlan(OverlayHostWindow Host, bool Visible, PixelRect Bounds, nint Target, DpiScale Dpi);

    private List<Exception> ApplyOnDispatcher(List<(WpfOverlayVisual Visual, OverlayFrame Frame)> batch)
    {
        var errors = new List<Exception>();
        if (Volatile.Read(ref _disposed) != 0) return errors;
        var plans = new List<HostPlan>();
        var touchedGroups = new HashSet<OverlayGroup>();
        var applied = new List<(WpfOverlayVisual Visual, OverlayFrame Frame)>();

        foreach (var (visual, frame) in batch)
        {
            if (visual.IsDisposed || !_visuals.Contains(visual)) continue;
            visual.SetFrame(frame);
            applied.Add((visual, frame));
            try
            {
                if (visual.Host is { } host)
                {
                    if (!host.IsClosed)
                        plans.Add(new HostPlan(host, frame.Visible && !frame.Bounds.IsEmpty, frame.Bounds, frame.TargetWindow, frame.Dpi));
                }
                else if (_options.HostingMode == WpfHostingMode.Grouped)
                {
                    AssignGroup(visual, frame, touchedGroups);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        foreach (var group in touchedGroups)
        {
            if (group.Members.Count == 0) CloseGroup(group);
            else if (!group.Window.IsClosed) plans.Add(PlanGroup(group));
        }

        ExecutePlans(plans, errors);

        foreach (var (visual, frame) in applied)
        {
            if (visual.Context.Update(frame) is { } handlerError)
                _logger.LogWarning(handlerError, "A TargetBoundsChanged handler of WPF overlay {RuleId} threw", visual.RuleId);
        }
        return errors;
    }

    private void ExecutePlans(List<HostPlan> plans, List<Exception> errors)
    {
        if (plans.Count == 0) return;

        // 1. Hide what must disappear.
        foreach (var p in plans.Where(p => !p.Visible))
        {
            try
            {
                p.Host.HideWindow();
                p.Host.AppliedBounds = null;
            }
            catch (Exception ex) { errors.Add(ex); }
        }

        var visible = plans.Where(p => p.Visible).ToList();
        if (visible.Count == 0) return;

        // 2. Move, size and stack every visible host in one DeferWindowPos transaction (no tearing between overlays).
        try { Position(visible); }
        catch (Exception ex) { errors.Add(ex); }

        // 3. Size the content for the scale WPF renders each host at. Layout and rendering happen after this dispatcher
        //    invocation returns, so no frame is presented with stale sizes.
        foreach (var p in visible)
        {
            try { p.Host.Relayout?.Invoke(p.Host.RenderScale(p.Dpi)); }
            catch (Exception ex) { errors.Add(ex); }
        }

        // 4. Show hosts that were hidden, without activating them.
        var shown = new List<HostPlan>();
        foreach (var p in visible.Where(p => !p.Host.IsShown))
        {
            try
            {
                p.Host.ShowNoActivate();
                shown.Add(p);
            }
            catch (Exception ex) { errors.Add(ex); }
        }

        // 5. Showing a WPF window for the first time can re-apply WPF's own notion of size/position; re-assert ours.
        if (shown.Count > 0)
        {
            try
            {
                Position(shown);
                foreach (var p in shown) p.Host.Relayout?.Invoke(p.Host.RenderScale(p.Dpi));
            }
            catch (Exception ex) { errors.Add(ex); }
        }
    }

    private void Position(List<HostPlan> plans)
    {
        var ops = plans
            .Where(p => !p.Host.IsClosed && p.Host.Handle != 0)
            .Select(p =>
            {
                var insertAfter = InsertAfter(p);
                uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER;
                if (insertAfter is null) flags |= NativeMethods.SWP_NOZORDER;
                return (Plan: p, InsertAfter: insertAfter ?? 0, Flags: flags);
            })
            .ToList();
        if (ops.Count == 0) return;

        bool batched = false;
        if (ops.Count > 1)
        {
            var hdwp = NativeMethods.BeginDeferWindowPos(ops.Count);
            foreach (var op in ops)
            {
                if (hdwp == 0) break;
                var b = op.Plan.Bounds;
                // On failure DeferWindowPos frees the structure and returns 0: fall back to individual calls below.
                hdwp = NativeMethods.DeferWindowPos(hdwp, op.Plan.Host.Handle, op.InsertAfter, b.X, b.Y, b.Width, b.Height, op.Flags);
            }
            batched = hdwp != 0 && NativeMethods.EndDeferWindowPos(hdwp);
        }
        if (!batched)
        {
            foreach (var op in ops)
            {
                var b = op.Plan.Bounds;
                if (!NativeMethods.SetWindowPos(op.Plan.Host.Handle, op.InsertAfter, b.X, b.Y, b.Width, b.Height, op.Flags))
                    _logger.LogDebug("SetWindowPos failed for overlay host 0x{Handle:X} (error {Error})", op.Plan.Host.Handle, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            }
        }
        foreach (var op in ops) op.Plan.Host.AppliedBounds = op.Plan.Bounds;
    }

    /// <summary>The hWndInsertAfter for a host, or null to leave its z-order unchanged.</summary>
    private static nint? InsertAfter(HostPlan p)
    {
        if (p.Host.ZOrder == OverlayZOrder.Topmost) return NativeMethods.HWND_TOPMOST;
        var target = p.Target;
        if (target == 0 || !NativeMethods.IsWindow(target)) return null;
        // A non-topmost window cannot be stacked above a topmost target without becoming topmost itself.
        if ((NativeMethods.GetExStyle(target) & NativeMethods.WS_EX_TOPMOST) != 0) return NativeMethods.HWND_TOPMOST;
        // "Insert after X" places the window directly below X; inserting after the window above the target therefore
        // places the overlay directly above the target.
        var prev = NativeMethods.GetWindow(target, NativeMethods.GW_HWNDPREV);
        if (prev == p.Host.Handle) return null; // already directly above the target
        if (prev == 0) return NativeMethods.HWND_TOP;
        // The target is the first non-topmost window: become the top of the non-topmost band.
        if ((NativeMethods.GetExStyle(prev) & NativeMethods.WS_EX_TOPMOST) != 0) return NativeMethods.HWND_TOP;
        return prev;
    }

    private static void LayoutSingle(WpfOverlayVisual visual, double scale)
    {
        if (!visual.IsShowing(out var b)) return;
        visual.Root.Width = b.Width / scale;
        visual.Root.Height = b.Height / scale;
    }

    // ------------------------------------------------------------------ grouped hosting

    private void AssignGroup(WpfOverlayVisual visual, OverlayFrame frame, HashSet<OverlayGroup> touched)
    {
        bool showing = frame.Visible && !frame.Bounds.IsEmpty;
        if (visual.Group is { } current)
        {
            // Hidden frames carry no target: stay in the current group (collapsed) for a cheap re-show.
            if (!showing || current.Key.Target == frame.TargetWindow)
            {
                touched.Add(current);
                return;
            }
            RemoveFromGroup(visual, current);
            touched.Add(current);
        }
        if (!showing) return;

        var request = visual.Request;
        var key = new OverlayGroupKey(frame.TargetWindow, request.InputMode, request.FocusPolicy, request.ZOrder);
        if (!_groups.TryGetValue(key, out var group) || group.Window.IsClosed) group = CreateGroup(key);
        group.Members.Add(visual);
        group.Canvas.Children.Add(visual.Root);
        visual.Group = group;
        visual.SetHandle(group.Window.Handle);
        touched.Add(group);
    }

    private OverlayGroup CreateGroup(OverlayGroupKey key)
    {
        // No background anywhere: pixels between members stay fully transparent, so even the interactive host lets clicks
        // in the gaps through to the target.
        var canvas = new Canvas { Background = null, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
        var window = new OverlayHostWindow(key.InputMode, key.FocusPolicy, key.ZOrder, background: null) { Content = canvas };
        var group = new OverlayGroup(key, window, canvas);
        window.Relayout = scale => LayoutGroup(group, scale);
        window.Closed += (_, _) => OnGroupClosed(group);
        try
        {
            Register(window.EnsureCreated());
        }
        catch
        {
            CloseHost(window);
            throw;
        }
        _groups[key] = group;
        return group;
    }

    private static HostPlan PlanGroup(OverlayGroup group)
    {
        var union = PixelRect.Empty;
        DpiScale? dpi = null;
        foreach (var m in group.Members)
        {
            if (!m.IsShowing(out var b)) continue;
            union = union.Union(b);
            dpi ??= m.CurrentFrame!.Dpi;
        }
        return new HostPlan(group.Window, !union.IsEmpty, union, group.Key.Target, dpi ?? DpiScale.Default);
    }

    private static void LayoutGroup(OverlayGroup group, double scale)
    {
        if (group.Window.AppliedBounds is not { IsEmpty: false } origin) return;
        group.Canvas.Width = origin.Width / scale;
        group.Canvas.Height = origin.Height / scale;
        foreach (var m in group.Members)
        {
            if (!m.IsShowing(out var b))
            {
                m.Root.Visibility = Visibility.Collapsed;
                continue;
            }
            Canvas.SetLeft(m.Root, (b.X - origin.X) / scale);
            Canvas.SetTop(m.Root, (b.Y - origin.Y) / scale);
            m.Root.Width = b.Width / scale;
            m.Root.Height = b.Height / scale;
            m.Root.Visibility = Visibility.Visible;
        }
    }

    private static void RemoveFromGroup(WpfOverlayVisual visual, OverlayGroup group)
    {
        group.Members.Remove(visual);
        group.Canvas.Children.Remove(visual.Root);
        visual.Group = null;
        visual.SetHandle(0);
    }

    private void CloseGroup(OverlayGroup group)
    {
        if (_groups.TryGetValue(group.Key, out var g) && ReferenceEquals(g, group)) _groups.Remove(group.Key);
        CloseHost(group.Window);
    }

    private void OnGroupClosed(OverlayGroup group)
    {
        Unregister(group.Window.Handle);
        if (_groups.TryGetValue(group.Key, out var g) && ReferenceEquals(g, group)) _groups.Remove(group.Key);
        foreach (var m in group.Members.ToArray()) RemoveFromGroup(m, group);
    }

    // ------------------------------------------------------------------ disposal

    internal async ValueTask DisposeVisualAsync(WpfOverlayVisual visual)
    {
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            // The dispatcher shutdown destroyed the windows already; only the registry needs cleaning.
            ForgetHandles(visual);
            return;
        }
        try
        {
            await DispatcherInvoke.RunAsync(dispatcher, () => { DisposeOnDispatcher(visual); return true; }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WindowVeilException ex) when (ex.Code == WindowVeilErrorCode.WpfDispatcherFailure)
        {
            ForgetHandles(visual);
        }
    }

    private void ForgetHandles(WpfOverlayVisual visual)
    {
        if (visual.Host is { } host) Unregister(host.Handle);
        else if (_options.HostingMode == WpfHostingMode.PerOverlay && visual.Handle != 0) Unregister(visual.Handle);
    }

    private void DisposeOnDispatcher(WpfOverlayVisual visual)
    {
        _visuals.Remove(visual);
        if (visual.Host is { } host)
        {
            CloseHost(host);
        }
        else if (visual.Group is { } group)
        {
            RemoveFromGroup(visual, group);
            if (group.Members.Count == 0) CloseGroup(group);
            else
            {
                var errors = new List<Exception>();
                ExecutePlans([PlanGroup(group)], errors);
                foreach (var e in errors) _logger.LogWarning(e, "Failed to re-layout a shared WPF overlay host");
            }
        }
        visual.Root.Children.Clear();
    }

    private void CloseHost(OverlayHostWindow host)
    {
        var handle = host.Handle;
        if (!host.IsClosed)
        {
            try { host.Close(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.LogWarning(ex, "Failed to close WPF overlay host 0x{Handle:X}", handle);
            }
        }
        Unregister(handle);
    }

    /// <summary>
    /// Closes every overlay window and, when the renderer owns its dispatcher thread, shuts it down. Idempotent and safe
    /// after the dispatcher has already shut down. A supplied dispatcher is left running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Dispatcher? dispatcher;
        lock (_startLock) dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            try
            {
                await DispatcherInvoke.RunAsync(dispatcher, () =>
                {
                    foreach (var v in _visuals.ToArray())
                    {
                        v.MarkDisposed();
                        DisposeOnDispatcher(v);
                    }
                    foreach (var g in _groups.Values.ToArray()) CloseGroup(g);
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (WindowVeilException ex)
            {
                _logger.LogDebug(ex, "The WPF dispatcher shut down before the overlays could be closed");
            }
        }
        foreach (var h in _handles.Keys) Unregister(h);
        WpfDispatcherHost? host;
        lock (_startLock) host = _ownedHost;
        if (host is not null) await host.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ helpers

    private Dispatcher GetDispatcher()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _dispatcher) is { } d) return d;
        lock (_startLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_dispatcher is null)
            {
                _ownedHost = new WpfDispatcherHost(_options.ThreadName, _logger);
                Volatile.Write(ref _dispatcher, _ownedHost.Dispatcher);
            }
            return _dispatcher!;
        }
    }

    private void Register(nint handle)
    {
        if (handle == 0) return;
        _handles[handle] = 0;
        OverlayWindowRegistry.Register(handle);
    }

    private void Unregister(nint handle)
    {
        if (handle == 0) return;
        _handles.TryRemove(handle, out _);
        OverlayWindowRegistry.Unregister(handle);
    }

    private static Brush? ToBrush(Color color)
    {
        if (color.A == 0) return null;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
