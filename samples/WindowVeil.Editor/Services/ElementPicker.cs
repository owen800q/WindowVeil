using WindowVeil.Automation;
using WindowVeil.Editor.Model;
using WindowVeil.Geometry;
using WindowVeil.Rendering;

namespace WindowVeil.Editor.Services;

/// <summary>
/// Hit testing for the crosshair picker. Cursor positions are physical screen pixels. At most one
/// <see cref="UiAutomationInspector.ElementFromPointAsync(PixelPoint, CancellationToken)"/> query is in flight; moves
/// that arrive meanwhile replace each other and only the latest is queried next. Elements of the editor's own process
/// and WindowVeil overlay windows are ignored.
/// </summary>
public sealed class ElementPicker
{
    private readonly UiAutomationInspector _inspector;
    private readonly SynchronizationContext? _context;
    private readonly LatestOnlyWorker<PixelPoint> _worker;
    private readonly int _ownProcessId = Environment.ProcessId;
    private PixelPoint? _lastPosted;
    private string? _lastRuntimeId;

    /// <summary>Creates a picker. <see cref="Hovered"/> is raised on <paramref name="context"/> (normally the UI thread).</summary>
    public ElementPicker(UiAutomationInspector inspector, SynchronizationContext? context, Action<Exception>? onError = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _context = context;
        _worker = new LatestOnlyWorker<PixelPoint>(QueryAsync, onError);
    }

    /// <summary>Raised when the hovered element changes (null = nothing pickable under the cursor).</summary>
    public event EventHandler<AutomationElementInfo?>? Hovered;

    /// <summary>Starts a picking gesture.</summary>
    public void Begin()
    {
        _lastPosted = null;
        _lastRuntimeId = null;
    }

    /// <summary>Reports a cursor move (physical pixels). Cheap; never blocks.</summary>
    public void Move(PixelPoint screenPoint)
    {
        if (_lastPosted == screenPoint) return;
        _lastPosted = screenPoint;
        _worker.Post(screenPoint);
    }

    /// <summary>Ends the gesture at <paramref name="screenPoint"/> and returns the element there (null when none).</summary>
    public async Task<AutomationElementInfo?> CompleteAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default)
    {
        await _worker.CancelAsync().ConfigureAwait(false);
        var info = await _inspector.ElementFromPointAsync(screenPoint, cancellationToken).ConfigureAwait(false);
        return Filter(info);
    }

    /// <summary>Cancels the gesture.</summary>
    public Task CancelAsync() => _worker.CancelAsync();

    /// <summary>True when the element may be picked (not the editor itself, not an overlay).</summary>
    public bool IsPickable(AutomationElementInfo? info) =>
        info is not null && info.ProcessId != _ownProcessId &&
        (info.TopLevelWindowHandle == 0 || !OverlayWindowRegistry.IsOverlay(info.TopLevelWindowHandle));

    private AutomationElementInfo? Filter(AutomationElementInfo? info) => IsPickable(info) ? info : null;

    private async Task QueryAsync(PixelPoint point, CancellationToken ct)
    {
        var info = Filter(await _inspector.ElementFromPointAsync(point, ct).ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();
        string? id = info is null ? null : info.RuntimeId + "|" + info.Bounds;
        if (id == _lastRuntimeId) return;
        _lastRuntimeId = id;
        Raise(info);
    }

    private void Raise(AutomationElementInfo? info)
    {
        if (_context is null) Hovered?.Invoke(this, info);
        else _context.Post(_ => Hovered?.Invoke(this, info), null);
    }
}
