using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Locators;
using WindowVeil.Platform;

namespace WindowVeil.Automation;

/// <summary>
/// Resolves <see cref="UiAutomationLocator"/> and <see cref="TextLocator"/> against external windows using the
/// managed UI Automation client (<c>System.Windows.Automation</c>).
/// </summary>
/// <remarks>
/// <para>All UIA calls run on a private <see cref="UiAutomationThread"/> (MTA, per-monitor v2 DPI aware), so every
/// rectangle is in physical virtual-desktop pixels. Resolutions are serialized on that thread; the resolver is
/// thread-safe.</para>
/// <para>Searches are breadth-first and bounded by <see cref="UiAutomationLocator.MaxDepth"/> and
/// <see cref="UiAutomationLocator.MaxNodes"/> (text locators by <see cref="UiAutomationOptions.TextSearchMaxDepth"/> and
/// <see cref="UiAutomationOptions.TextSearchMaxNodes"/>). The cancellation token is checked between nodes.</para>
/// <para>Unresolved outcomes are explicit: <see cref="LocatorStatus.NotFound"/> (with the selector, nodes searched and
/// whether a bound was hit), <see cref="LocatorStatus.Offscreen"/> (element exists but <c>IsOffscreen</c> or has no
/// bounds) and <see cref="LocatorStatus.Unavailable"/> (no UI Automation element for the window).</para>
/// <para>With <see cref="UiAutomationOptions.CacheElements"/>, the found element is kept in
/// <see cref="LocatorResolution.State"/>. The next resolution refreshes its properties in one call and reuses it when it
/// is still alive, still in the same window and still matches the selector; otherwise the tree is searched again.
/// A cached element is not reused for <see cref="ResolveReason.Reconnected"/> and <see cref="ResolveReason.Manual"/>
/// resolutions. Note that for <see cref="UiAutomationLocator.Index"/> &gt; 0 or <see cref="UiAutomationLocator.Ancestor"/>
/// selectors the re-validation checks only the element's own properties.</para>
/// </remarks>
public sealed class UiAutomationLocatorResolver : ILocatorResolver, IDisposable
{
    private readonly UiAutomationThread _thread;
    private readonly ILogger _logger;
    private CacheRequest? _request; // created and used on the UIA thread only
    private volatile bool _disposed;

    /// <summary>Creates a resolver with its own UI Automation thread.</summary>
    /// <param name="options">Options (defaults when null).</param>
    /// <param name="logger">Logger (none when null).</param>
    public UiAutomationLocatorResolver(UiAutomationOptions? options = null, ILogger? logger = null)
    {
        Options = options ?? new UiAutomationOptions();
        Options.Validate();
        _logger = logger ?? NullLogger.Instance;
        _thread = new UiAutomationThread("WindowVeil UIA resolver", _logger);
    }

    /// <summary>Options in effect.</summary>
    public UiAutomationOptions Options { get; }

    /// <inheritdoc />
    public bool CanResolve(ElementLocator locator) => locator is UiAutomationLocator or TextLocator;

    /// <inheritdoc />
    public async ValueTask<LocatorResolution> ResolveAsync(ElementLocator locator, LocatorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        switch (locator)
        {
            case UiAutomationLocator u:
                if (Validate(u) is { } invalid) return LocatorResolution.Failed(locator, LocatorStatus.Error, invalid);
                return await _thread.InvokeAsync(ct => ResolveElement(u, context, ct), cancellationToken).ConfigureAwait(false);
            case TextLocator t:
                if (t.Scope is not null && Validate(t.Scope) is { } invalidScope)
                    return LocatorResolution.Failed(locator, LocatorStatus.Error, "Invalid text scope: " + invalidScope);
                return await _thread.InvokeAsync(ct => ResolveText(t, context, ct), cancellationToken).ConfigureAwait(false);
            default:
                return LocatorResolution.Failed(locator, LocatorStatus.Unsupported, $"The UI Automation resolver does not handle '{locator.Kind}' locators.");
        }
    }

    /// <summary>Stops the UI Automation thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thread.Dispose();
    }

    private static string? Validate(UiAutomationLocator u)
    {
        for (var s = u; s is not null; s = s.Ancestor)
        {
            if (s.IsEmpty) return $"The UI Automation selector {s} has no conditions (AutomationId, Name, ControlType or ClassName).";
            if (s.Index < 0) return $"Index must not be negative in {s}.";
            if (s.MaxDepth < 0) return $"MaxDepth must not be negative in {s}.";
            if (s.MaxNodes <= 0) return $"MaxNodes must be positive in {s}.";
        }
        return null;
    }

    private CacheRequest Request => _request ??= UiaHelpers.CreateSearchCacheRequest(Options.UseRawView);

    private static IReadOnlySet<nint> Overlays(LocatorContext context) => context.OverlayWindows;

    private static LocatorResolution UnavailableFor(ElementLocator locator, TargetWindowInfo window, Exception ex) =>
        LocatorResolution.Failed(locator, LocatorStatus.Unavailable,
            $"UI Automation unavailable for window 0x{window.Handle:X} '{window.Title}': {ex.Message}", ex);

    private bool TryGetRoot(LocatorContext context, out AutomationElement root, out Exception? error)
    {
        root = null!;
        error = null;
        try
        {
            root = AutomationElement.FromHandle(context.Window.Handle);
            return true;
        }
        catch (Exception ex) when (UiaHelpers.IsStale(ex) || ex is ArgumentException)
        {
            error = ex;
            return false;
        }
    }

    // ---------------------------------------------------------------- element locators (UIA thread)

    private LocatorResolution ResolveElement(UiAutomationLocator locator, LocatorContext context, CancellationToken ct)
    {
        var request = Request;
        var previous = context.Previous?.State as UiAutomationElementState;

        if (Options.CacheElements && previous is not null && CanReuse(previous, locator, context)
            && TryReuse(previous, locator, context, request) is { } reused)
            return reused;

        ct.ThrowIfCancellationRequested();
        if (!TryGetRoot(context, out var root, out var error)) return UnavailableFor(locator, context.Window, error!);

        SearchOutcome outcome;
        try
        {
            outcome = ElementSearch.Find(root, locator, request, Overlays(context), ct);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException)
        {
            return UnavailableFor(locator, context.Window, ex);
        }

        if (outcome.Element is null)
        {
            var message = outcome.Describe(locator);
            _logger.LogDebug("UIA resolution failed for window 0x{Handle:X}: {Message}", context.Window.Handle, message);
            return LocatorResolution.Failed(locator, LocatorStatus.NotFound, message);
        }

        UiAutomationElementState? state = null;
        if (Options.CacheElements)
        {
            string runtimeId = "";
            try { runtimeId = UiaHelpers.RuntimeIdKey(outcome.Element.GetRuntimeId()); }
            catch (Exception ex) when (UiaHelpers.IsStale(ex)) { }
            state = new UiAutomationElementState(outcome.Element, locator, context.Window.Handle, runtimeId);
        }
        return FromElement(outcome.Element, locator, context, state, $"Found after searching {outcome.Visited} node(s).");
    }

    private static bool CanReuse(UiAutomationElementState previous, UiAutomationLocator locator, LocatorContext context) =>
        !previous.IsDisposed
        && previous.WindowHandle == context.Window.Handle
        && context.Reason is not (ResolveReason.Reconnected or ResolveReason.Manual)
        && Equals(previous.Locator, locator);

    private LocatorResolution? TryReuse(UiAutomationElementState previous, UiAutomationLocator locator, LocatorContext context, CacheRequest request)
    {
        var element = previous.Element;
        if (element is null) return null;
        try
        {
            var fresh = element.GetUpdatedCache(request); // one round trip; throws when the element is gone
            if (!UiaHelpers.MatchesCached(fresh, locator))
            {
                _logger.LogDebug("Cached UIA element {RuntimeId} no longer matches {Locator}; searching again.", previous.RuntimeId, locator);
                return null;
            }
            return FromElement(fresh, locator, context, previous, "Cached element re-validated.");
        }
        catch (Exception ex) when (UiaHelpers.IsStale(ex))
        {
            _logger.LogDebug("Cached UIA element {RuntimeId} is stale ({Error}); searching again.", previous.RuntimeId, ex.GetType().Name);
            return null;
        }
    }

    private static LocatorResolution FromElement(AutomationElement element, UiAutomationLocator locator, LocatorContext context,
        UiAutomationElementState? state, string detail)
    {
        var c = element.Cached;
        var rect = c.BoundingRectangle;
        if (c.IsOffscreen || !UiaHelpers.TryToPixelRect(rect, out var bounds))
        {
            var why = c.IsOffscreen ? "IsOffscreen is true" : $"its bounding rectangle is empty ({rect})";
            return LocatorResolution.Failed(locator, LocatorStatus.Offscreen,
                $"Element {locator} exists but is not on screen: {why} (collapsed, scrolled out of view or hidden).") with { State = state };
        }
        var visible = UiaHelpers.ClipToVisible(element, bounds, context.Window.Handle, context.Window.Bounds);
        if (visible.IsEmpty)
            return LocatorResolution.Failed(locator, LocatorStatus.Offscreen,
                $"Element {locator} exists but is scrolled out of view (its rectangle {bounds} is outside every scrolling ancestor or the window).") with { State = state };
        if (visible != bounds) detail += $" Clipped to the visible area {visible} (element rectangle {bounds}).";
        return LocatorResolution.Found(locator, visible, 1, detail) with { WindowBounds = context.Window.Bounds, State = state };
    }

    // ---------------------------------------------------------------- text locators (UIA thread)

    private LocatorResolution ResolveText(TextLocator locator, LocatorContext context, CancellationToken ct)
    {
        var request = Request;
        if (!TryGetRoot(context, out var root, out var error)) return UnavailableFor(locator, context.Window, error!);

        var scope = root;
        if (locator.Scope is { } scopeSelector)
        {
            SearchOutcome outcome;
            try { outcome = ElementSearch.Find(root, scopeSelector, request, Overlays(context), ct); }
            catch (ElementNotAvailableException ex) { return UnavailableFor(locator, context.Window, ex); }
            if (outcome.Element is null)
                return LocatorResolution.Failed(locator, LocatorStatus.NotFound, "Text scope not found: " + outcome.Describe(scopeSelector));
            scope = outcome.Element;
        }

        TextSearch.Result result;
        try { result = TextSearch.Find(scope, locator, Options, request, Overlays(context), ct, context.Window.Handle, context.Window.Bounds); }
        catch (ElementNotAvailableException ex) { return UnavailableFor(locator, context.Window, ex); }

        if (result.Status == LocatorStatus.Resolved)
            return LocatorResolution.Found(locator, result.Bounds, result.Confidence, result.Message) with { WindowBounds = context.Window.Bounds };
        _logger.LogDebug("UIA text resolution failed for window 0x{Handle:X}: {Message}", context.Window.Handle, result.Message);
        return LocatorResolution.Failed(locator, result.Status, result.Message);
    }
}
