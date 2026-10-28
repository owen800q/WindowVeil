using WindowVeil.Geometry;
using WindowVeil.Text;

namespace WindowVeil.Locators;

/// <summary>
/// Describes <i>how</i> to find the screen region an overlay is attached to. Locators are immutable data;
/// they are resolved by <see cref="ILocatorResolver"/> implementations registered with the engine
/// (rectangles are resolved by the core, UI Automation by <c>WindowVeil.Automation</c>, OCR by <c>WindowVeil.Ocr</c>).
/// </summary>
public abstract record ElementLocator
{
    /// <summary>Short, stable identifier of the locator kind (for diagnostics and profiles), e.g. <c>uiAutomation</c>.</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// An absolute rectangle in physical virtual-desktop screen coordinates. It does not follow the target window.
    /// </summary>
    public static ElementLocator Rectangle(int x, int y, int width, int height) =>
        new ScreenRectangleLocator(new PixelRect(x, y, width, height));

    /// <summary>
    /// A rectangle in physical pixels relative to the target window's outer bounds or its client area.
    /// It follows the window as it moves.
    /// </summary>
    public static ElementLocator WindowRelative(int x, int y, int width, int height, WindowAnchor anchor = WindowAnchor.Client) =>
        new WindowRelativeLocator(new PixelRect(x, y, width, height), anchor);

    /// <summary>
    /// A rectangle expressed as fractions (0..1) of the target window or client size. It follows moves and scales with resizes.
    /// </summary>
    public static ElementLocator Normalized(double x, double y, double width, double height, WindowAnchor anchor = WindowAnchor.Client) =>
        new NormalizedLocator(new RectD(x, y, width, height), anchor);

    /// <summary>The whole target window (or client area).</summary>
    public static ElementLocator Window(WindowAnchor anchor = WindowAnchor.Window) => new NormalizedLocator(new RectD(0, 0, 1, 1), anchor);

    /// <summary>A UI Automation element with the given AutomationId.</summary>
    public static UiAutomationLocator ByAutomationId(string automationId) => new() { AutomationId = automationId };

    /// <summary>A UI Automation element whose Name property matches.</summary>
    public static UiAutomationLocator ByName(string name, TextMatch match = TextMatch.Exact, bool ignoreCase = false) =>
        new() { Name = new TextQuery(name, match, ignoreCase) };

    /// <summary>A UI Automation element of the given control type (programmatic name such as <c>CheckBox</c> or <c>Button</c>).</summary>
    public static UiAutomationLocator ByControlType(string controlType) => new() { ControlType = controlType };

    /// <summary>A UI Automation element with the given class name.</summary>
    public static UiAutomationLocator ByClassName(string className) => new() { ClassName = className };

    /// <summary>
    /// Text exposed through UI Automation: TextPattern ranges where supported, otherwise the Name of text elements.
    /// Only the matched text is located, not the whole containing control.
    /// </summary>
    public static TextLocator ByText(string text, TextMatch match = TextMatch.Contains, bool ignoreCase = true) =>
        new(new TextQuery(text, match, ignoreCase));

    /// <summary>Text recognized by OCR in a capture of the target window.</summary>
    public static OcrTextLocator ByOcrText(string text, TextMatch match = TextMatch.Contains, bool ignoreCase = true) =>
        new(new TextQuery(text, match, ignoreCase));

    /// <summary>Tries each locator in order and uses the first one that resolves.</summary>
    public static FallbackLocator FirstOf(params ElementLocator[] locators) => new(locators);

    /// <summary>
    /// Builds the default strategy chain: UI Automation (AutomationId, then Name), then TextPattern text,
    /// then OCR text, then an optional manual rectangle. Omitted parts are skipped.
    /// </summary>
    public static FallbackLocator Smart(
        string? automationId = null,
        string? name = null,
        string? text = null,
        string? ocrText = null,
        PixelRect? windowRelativeFallback = null)
    {
        var chain = new List<ElementLocator>();
        if (automationId is not null) chain.Add(ByAutomationId(automationId));
        if (name is not null) chain.Add(ByName(name));
        if (text is not null) chain.Add(ByText(text));
        if (ocrText is not null) chain.Add(ByOcrText(ocrText));
        if (windowRelativeFallback is { } r) chain.Add(WindowRelative(r.X, r.Y, r.Width, r.Height));
        if (chain.Count == 0) throw new ArgumentException("At least one locator strategy must be specified.");
        return new FallbackLocator(chain);
    }

    /// <summary>Returns a fallback chain that tries this locator, then <paramref name="next"/>.</summary>
    public FallbackLocator Or(ElementLocator next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var items = this is FallbackLocator f ? f.Locators.ToList() : [this];
        if (next is FallbackLocator nf) items.AddRange(nf.Locators); else items.Add(next);
        return new FallbackLocator(items);
    }

    /// <summary>Whether the located geometry depends on the target window position.</summary>
    public virtual bool IsWindowDependent => true;
}

/// <summary>Which window rectangle a relative locator is measured from.</summary>
public enum WindowAnchor
{
    /// <summary>The window's outer bounds (including non-client frame).</summary>
    Window,

    /// <summary>The window's client area.</summary>
    Client,
}

/// <summary>An absolute screen rectangle (physical pixels).</summary>
public sealed record ScreenRectangleLocator(PixelRect Bounds) : ElementLocator
{
    /// <inheritdoc />
    public override string Kind => "rectangle";

    /// <inheritdoc />
    public override bool IsWindowDependent => false;
}

/// <summary>A rectangle relative to the window or client origin (physical pixels).</summary>
public sealed record WindowRelativeLocator(PixelRect Bounds, WindowAnchor Anchor) : ElementLocator
{
    /// <inheritdoc />
    public override string Kind => "windowRelative";
}

/// <summary>A rectangle as fractions of the window or client size.</summary>
public sealed record NormalizedLocator(RectD Bounds, WindowAnchor Anchor) : ElementLocator
{
    /// <inheritdoc />
    public override string Kind => "normalized";
}

/// <summary>
/// A UI Automation selector. All specified properties must match. Matching is bounded by
/// <see cref="MaxDepth"/> and <see cref="MaxNodes"/> so a huge accessibility tree cannot stall the session.
/// </summary>
public sealed record UiAutomationLocator : ElementLocator
{
    /// <inheritdoc />
    public override string Kind => "uiAutomation";

    /// <summary>Exact AutomationId (WPF exposes <c>x:Name</c> here unless overridden).</summary>
    public string? AutomationId { get; init; }

    /// <summary>Name property query.</summary>
    public TextQuery? Name { get; init; }

    /// <summary>Control type programmatic name, e.g. <c>CheckBox</c> (case-insensitive, <c>ControlType.</c> prefix optional).</summary>
    public string? ControlType { get; init; }

    /// <summary>Exact class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Optional selector the element must be a descendant of.</summary>
    public UiAutomationLocator? Ancestor { get; init; }

    /// <summary>Which match to use when several elements match (0-based, in tree order). Default 0.</summary>
    public int Index { get; init; }

    /// <summary>Maximum tree depth searched below the window. Default 32.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Maximum number of elements visited per search. Default 5000.</summary>
    public int MaxNodes { get; init; } = 5000;

    /// <summary>Adds a Name condition.</summary>
    public UiAutomationLocator WithName(string name, TextMatch match = TextMatch.Exact, bool ignoreCase = false) =>
        this with { Name = new TextQuery(name, match, ignoreCase) };

    /// <summary>Adds a ControlType condition.</summary>
    public UiAutomationLocator WithControlType(string controlType) => this with { ControlType = controlType };

    /// <summary>Adds a ClassName condition.</summary>
    public UiAutomationLocator WithClassName(string className) => this with { ClassName = className };

    /// <summary>Restricts the search to descendants of another element.</summary>
    public UiAutomationLocator Within(UiAutomationLocator ancestor) => this with { Ancestor = ancestor };

    /// <summary>Selects the n-th match.</summary>
    public UiAutomationLocator At(int index) => this with { Index = index };

    /// <summary>True when no condition is specified.</summary>
    public bool IsEmpty => AutomationId is null && Name is null && ControlType is null && ClassName is null;

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>();
        if (AutomationId is not null) parts.Add($"AutomationId='{AutomationId}'");
        if (Name is not null) parts.Add($"Name {Name}");
        if (ControlType is not null) parts.Add($"ControlType={ControlType}");
        if (ClassName is not null) parts.Add($"ClassName={ClassName}");
        if (Index != 0) parts.Add($"Index={Index}");
        var s = "UIA(" + string.Join(", ", parts) + ")";
        return Ancestor is null ? s : $"{Ancestor} > {s}";
    }
}

/// <summary>
/// Text located through UI Automation. Uses TextPattern bounding rectangles (one per line for multi-line matches)
/// when the element supports it, otherwise the bounds of text elements whose Name matches.
/// </summary>
public sealed record TextLocator(TextQuery Query) : ElementLocator
{
    /// <inheritdoc />
    public override string Kind => "text";

    /// <summary>Optional element to search within (default: the whole window).</summary>
    public UiAutomationLocator? Scope { get; init; }

    /// <summary>Whether all matches are returned (default) or only the first.</summary>
    public bool AllMatches { get; init; } = true;
}

/// <summary>Text located by OCR on a capture of the target window.</summary>
public sealed record OcrTextLocator(TextQuery Query) : ElementLocator
{
    /// <inheritdoc />
    public override string Kind => "ocrText";

    /// <summary>Minimum recognition confidence (0..1). Default 0.6.</summary>
    public double MinConfidence { get; init; } = 0.6;

    /// <summary>Optional window-relative (client) region to recognize instead of the whole client area.</summary>
    public PixelRect? SearchRegion { get; init; }

    /// <summary>
    /// Optional controlled expansion applied to the recognized text box, in DIPs. For example
    /// <c>new Insets(20, 2, 2, 2)</c> grows the box 20 DIP to the left to include a checkbox glyph.
    /// </summary>
    public Insets Expand { get; init; }

    /// <summary>
    /// Text that the expanded region must not overlap (validated against the same OCR pass). If the expansion would
    /// cover any of these, the resolution fails with <see cref="LocatorStatus.Rejected"/> instead of masking neighbors.
    /// </summary>
    public IReadOnlyList<TextQuery> MustNotOverlap { get; init; } = [];

    /// <summary>Minimum time between OCR passes for this locator. Default 1 second.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether all matches are returned (default) or only the first.</summary>
    public bool AllMatches { get; init; } = true;

    /// <summary>Returns a copy with an expansion.</summary>
    public OcrTextLocator Expanded(Insets expand, params TextQuery[] mustNotOverlap) =>
        this with { Expand = expand, MustNotOverlap = mustNotOverlap };
}

/// <summary>An ordered chain of locators; the first that resolves wins.</summary>
public sealed record FallbackLocator : ElementLocator
{
    /// <summary>Creates a chain.</summary>
    public FallbackLocator(IEnumerable<ElementLocator> locators)
    {
        ArgumentNullException.ThrowIfNull(locators);
        Locators = locators.ToArray();
        if (Locators.Count == 0) throw new ArgumentException("A fallback chain needs at least one locator.", nameof(locators));
        if (Locators.Any(l => l is null)) throw new ArgumentException("Locators cannot be null.", nameof(locators));
    }

    /// <inheritdoc />
    public override string Kind => "fallback";

    /// <summary>Locators in priority order.</summary>
    public IReadOnlyList<ElementLocator> Locators { get; }

    /// <summary>
    /// Whether a later strategy is tried when an earlier one fails with an error (not just "not found").
    /// Default true.
    /// </summary>
    public bool ContinueOnError { get; init; } = true;

    /// <inheritdoc />
    public override bool IsWindowDependent => Locators.Any(l => l.IsWindowDependent);

    /// <inheritdoc />
    public bool Equals(FallbackLocator? other) =>
        other is not null && ContinueOnError == other.ContinueOnError && Locators.SequenceEqual(other.Locators);

    /// <inheritdoc />
    public override int GetHashCode() => Locators.Aggregate(ContinueOnError.GetHashCode(), (h, l) => HashCode.Combine(h, l));
}
