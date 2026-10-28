using WindowVeil.Appearance;
using WindowVeil.Editor.Diagnostics;
using WindowVeil.Editor.Model;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Rendering;

namespace WindowVeil.Editor.Services;

/// <summary>
/// Draws the picker / selection highlight on the external application with WindowVeil itself — no separate overlay
/// implementation.
/// </summary>
/// <remarks>
/// <para><b>Approach.</b> The highlighter owns one private <see cref="OverlaySession"/> attached (by handle) to the
/// top-level window that contains the highlighted element, with a single rule: an absolute
/// <see cref="ElementLocator.Rectangle"/> locator (the element's physical bounding rectangle as reported by UI
/// Automation) and a <see cref="HighlightAppearance"/> drawn by the engine's native renderer. Moving the highlight is a
/// <see cref="OverlaySession.UpdateRule"/> with a new rectangle (a placement-only change, so the overlay window is
/// reused); hovering an element of another window re-attaches the session to that window. The rule is
/// <see cref="OverlayZOrder.Topmost"/> so the frame stays visible while the editor (which holds the mouse capture
/// during picking) is in front, and pass-through so it never intercepts input. The UI Automation inspector already
/// ignores WindowVeil overlay windows when hit testing, so the frame never "picks itself".</para>
/// <para>The session is separate from the live-preview session: highlights never appear in the rule list or in
/// exported profiles.</para>
/// <para>Requests are coalesced (<see cref="LatestOnlyWorker{T}"/>): only the newest highlight is applied.</para>
/// </remarks>
public sealed class ElementHighlighter : IAsyncDisposable
{
    private const string RuleId = "windowveil-editor-highlight";

    /// <summary>Colour of the live picker frame.</summary>
    public static readonly OverlayColor PickerColor = OverlayColor.Parse("#1E88E5");

    /// <summary>Colour of the selection / preview frame.</summary>
    public static readonly OverlayColor SelectionColor = OverlayColor.Parse("#FFB300");

    private static readonly SessionOptions Options = new()
    {
        AutoReconnect = false,
        ElementRefreshInterval = TimeSpan.Zero,
    };

    private readonly WindowVeilEngine _engine;
    private readonly DiagnosticLog _log;
    private readonly LatestOnlyWorker<Request?> _worker;
    private OverlaySession? _session; // worker only
    private nint _sessionWindow;      // worker only
    private int _generation;
    private bool _disposed;

    private sealed record Request(nint Window, PixelRect Bounds, OverlayColor Color);

    /// <summary>Creates a highlighter.</summary>
    public ElementHighlighter(WindowVeilEngine engine, DiagnosticLog log)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _worker = new LatestOnlyWorker<Request?>(ApplyAsync, ex => _log.Warn("highlight", ErrorMessages.Describe(ex)));
    }

    /// <summary>
    /// Shows a frame around <paramref name="bounds"/> (physical screen pixels) above <paramref name="topLevelWindow"/>.
    /// With a <paramref name="duration"/>, the frame disappears automatically unless another highlight replaced it.
    /// </summary>
    public void Show(nint topLevelWindow, PixelRect bounds, OverlayColor color, TimeSpan? duration = null)
    {
        if (_disposed) return;
        if (topLevelWindow == 0 || bounds.IsEmpty) { Hide(); return; }
        int generation = Interlocked.Increment(ref _generation);
        _worker.Post(new Request(topLevelWindow, bounds, color));
        if (duration is { } d) _ = HideLaterAsync(generation, d);
    }

    /// <summary>Hides the frame.</summary>
    public void Hide()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _generation);
        _worker.Post(null);
    }

    private async Task HideLaterAsync(int generation, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (Volatile.Read(ref _generation) == generation) Hide();
    }

    private async Task ApplyAsync(Request? request, CancellationToken ct)
    {
        if (request is null)
        {
            if (_session?.GetRule(RuleId) is { Enabled: true }) _session.DisableRule(RuleId);
            return;
        }

        if (_session is null || _sessionWindow != request.Window || _session.State is SessionState.TargetLost or SessionState.Disposed)
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            if (_engine.WindowSystem.GetWindow(request.Window) is null) return; // window vanished meanwhile
            _session = await _engine.AttachAsync(WindowSelector.ByHandle(request.Window), Options, ct).ConfigureAwait(false);
            _sessionWindow = request.Window;
            _session.AddRule(CreateRule(request));
            await _session.StartAsync(ct).ConfigureAwait(false);
            return;
        }

        var rule = CreateRule(request);
        if (_session.GetRule(RuleId) is null) _session.AddRule(rule);
        else _session.UpdateRule(rule);
    }

    private static OverlayRule CreateRule(Request request) => new()
    {
        Id = RuleId,
        Name = "Editor highlight",
        Target = ElementLocator.Rectangle(request.Bounds.X, request.Bounds.Y, request.Bounds.Width, request.Bounds.Height),
        Appearance = new HighlightAppearance(request.Color, thickness: 3),
        // 2 DIP padding draws the frame just outside the element so it does not hide the element's own border.
        Placement = OverlayPlacement.Cover(2),
        InputMode = OverlayInputMode.PassThrough,
        ZOrder = OverlayZOrder.Topmost,
    };

    private async Task DisposeSessionAsync()
    {
        var session = _session;
        _session = null;
        _sessionWindow = 0;
        if (session is null) return;
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn("highlight", "Disposing the highlight session failed: " + ex.Message); }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _worker.CancelAsync().ConfigureAwait(false);
        await DisposeSessionAsync().ConfigureAwait(false);
    }
}
