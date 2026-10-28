using WindowVeil.Geometry;

namespace WindowVeil.Locators;

/// <summary>Outcome category of a locator resolution.</summary>
public enum LocatorStatus
{
    /// <summary>The target was found; <see cref="LocatorResolution.Bounds"/> is non-empty.</summary>
    Resolved,

    /// <summary>The search completed but nothing matched.</summary>
    NotFound,

    /// <summary>The element exists but is currently off-screen, collapsed or scrolled out of view.</summary>
    Offscreen,

    /// <summary>A match was found but rejected (low OCR confidence, an expansion that would cover neighbors, ...).</summary>
    Rejected,

    /// <summary>The technology needed by the locator is unavailable (no UI Automation provider, no OCR, capture refused).</summary>
    Unavailable,

    /// <summary>No registered resolver supports this locator kind.</summary>
    Unsupported,

    /// <summary>The resolver failed unexpectedly.</summary>
    Error,

    /// <summary>The previous result is still valid and resolution was skipped (for example OCR rate limiting).</summary>
    Cached,
}

/// <summary>
/// The result of resolving an <see cref="ElementLocator"/>: zero or more screen rectangles (physical pixels),
/// which locator produced them and why resolution failed when it did. Resolvers never invent coordinates:
/// a failed resolution has no bounds.
/// </summary>
public sealed record LocatorResolution
{
    /// <summary>Status.</summary>
    public required LocatorStatus Status { get; init; }

    /// <summary>Matched rectangles in physical screen coordinates (several for multi-line text).</summary>
    public IReadOnlyList<PixelRect> Bounds { get; init; } = [];

    /// <summary>The locator that actually produced the result (for fallback chains, the winning entry).</summary>
    public ElementLocator? ResolvedBy { get; init; }

    /// <summary>Confidence in 0..1 (1 for exact accessibility geometry, recognition confidence for OCR).</summary>
    public double Confidence { get; init; } = 1;

    /// <summary>Diagnostic message for logs and the editor. Never contains recognized sensitive text by default.</summary>
    public string? Message { get; init; }

    /// <summary>Optional exception for <see cref="LocatorStatus.Error"/>.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Results of each attempted strategy in a fallback chain, in order.</summary>
    public IReadOnlyList<LocatorResolution> Attempts { get; init; } = [];

    /// <summary>
    /// Window position the bounds were computed for. Lets the session translate cached results when the window
    /// moves without resizing, avoiding an expensive re-resolve.
    /// </summary>
    public PixelRect? WindowBounds { get; init; }

    /// <summary>Opaque resolver state (for example a cached UI Automation element) reused by the next resolution.</summary>
    public object? State { get; init; }

    /// <summary>True when bounds are available.</summary>
    public bool IsResolved => (Status is LocatorStatus.Resolved or LocatorStatus.Cached) && Bounds.Count > 0;

    /// <summary>Union of all bounds.</summary>
    public PixelRect UnionBounds => PixelRect.UnionAll(Bounds);

    /// <summary>Creates a successful result.</summary>
    public static LocatorResolution Found(ElementLocator by, IReadOnlyList<PixelRect> bounds, double confidence = 1, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var nonEmpty = bounds.Where(b => !b.IsEmpty).ToArray();
        if (nonEmpty.Length == 0) return Failed(by, LocatorStatus.Offscreen, message ?? "The element has empty bounds.");
        return new LocatorResolution { Status = LocatorStatus.Resolved, Bounds = nonEmpty, ResolvedBy = by, Confidence = confidence, Message = message };
    }

    /// <summary>Creates a successful result with a single rectangle.</summary>
    public static LocatorResolution Found(ElementLocator by, PixelRect bounds, double confidence = 1, string? message = null) =>
        Found(by, [bounds], confidence, message);

    /// <summary>Creates a failed result.</summary>
    public static LocatorResolution Failed(ElementLocator? by, LocatorStatus status, string message, Exception? exception = null)
    {
        if (status is LocatorStatus.Resolved or LocatorStatus.Cached) throw new ArgumentException("Use Found for successful results.", nameof(status));
        return new LocatorResolution { Status = status, ResolvedBy = by, Message = message, Exception = exception, Confidence = 0 };
    }

    /// <summary>Returns a copy with every rectangle translated (used when the window moves without resizing).</summary>
    public LocatorResolution Translate(int dx, int dy) =>
        this with { Bounds = Bounds.Select(b => b.Offset(dx, dy)).ToArray(), WindowBounds = WindowBounds?.Offset(dx, dy) };
}
