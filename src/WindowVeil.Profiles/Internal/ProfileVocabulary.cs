using WindowVeil.Appearance;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WindowVeil.Text;

namespace WindowVeil.Profiles.Internal;

/// <summary>Bidirectional mapping between enum values and their camelCase profile spelling.</summary>
internal sealed class EnumMap<T> where T : struct, Enum
{
    private readonly Dictionary<string, T> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<T, string> _byValue = [];

    public EnumMap(params (string Name, T Value)[] entries)
    {
        foreach (var (name, value) in entries)
        {
            _byName.Add(name, value);
            _byValue.Add(value, name);
        }
        Names = entries.Select(e => e.Name).ToArray();
    }

    /// <summary>Canonical names in declaration order (used for error messages and the JSON schema).</summary>
    public IReadOnlyList<string> Names { get; }

    public bool TryParse(string text, out T value) => _byName.TryGetValue(text, out value);

    public string Format(T value) => _byValue.TryGetValue(value, out var s)
        ? s
        : throw new ArgumentOutOfRangeException(nameof(value), value, $"{typeof(T).Name} value is not representable in a profile.");

    public bool CanFormat(T value) => _byValue.ContainsKey(value);

    public string Describe() => string.Join(", ", Names.Select(n => $"'{n}'"));
}

/// <summary>Names, defaults and hard limits of profile schema v1, shared by the reader, the writer and the tests.</summary>
internal static class ProfileVocabulary
{
    public static readonly EnumMap<PlacementMode> PlacementModes = new(
        ("cover", PlacementMode.Cover), ("right", PlacementMode.Right), ("left", PlacementMode.Left),
        ("above", PlacementMode.Above), ("below", PlacementMode.Below), ("center", PlacementMode.Center));

    public static readonly EnumMap<PlacementAlignment> Alignments = new(
        ("start", PlacementAlignment.Start), ("center", PlacementAlignment.Center), ("end", PlacementAlignment.End));

    public static readonly EnumMap<MultiRectMode> MultiRectModes = new(("each", MultiRectMode.Each), ("union", MultiRectMode.Union));

    public static readonly EnumMap<OverlayInputMode> InputModes = new(
        ("passThrough", OverlayInputMode.PassThrough), ("interactive", OverlayInputMode.Interactive));

    public static readonly EnumMap<OverlayFocusPolicy> FocusPolicies = new(
        ("noActivate", OverlayFocusPolicy.NoActivate), ("activateOnClick", OverlayFocusPolicy.ActivateOnClick));

    public static readonly EnumMap<OverlayZOrder> ZOrders = new(("aboveTarget", OverlayZOrder.AboveTarget), ("topmost", OverlayZOrder.Topmost));

    public static readonly EnumMap<OverlayVisibility> Visibilities = new(
        ("whenTargetVisible", OverlayVisibility.WhenTargetVisible), ("whenTargetForeground", OverlayVisibility.WhenTargetForeground));

    public static readonly EnumMap<WindowAnchor> Anchors = new(("client", WindowAnchor.Client), ("window", WindowAnchor.Window));

    public static readonly EnumMap<TextMatch> TextMatches = new(
        ("exact", TextMatch.Exact), ("contains", TextMatch.Contains), ("startsWith", TextMatch.StartsWith), ("regex", TextMatch.Regex));

    public static readonly EnumMap<SpotlightScope> SpotlightScopes = new(
        ("targetWindow", SpotlightScope.TargetWindow), ("monitor", SpotlightScope.Monitor));

    public static readonly string[] LocatorTypes = ["rectangle", "windowRelative", "normalized", "uiAutomation", "text", "ocrText", "fallback"];

    public static readonly string[] AppearanceTypes = ["solid", "transparent", "highlight", "spotlight", "registered"];

    public static readonly string[] RootProperties = ["$schema", "schemaVersion", "name", "application", "rules"];

    public static readonly string[] ApplicationProperties = ["processName", "titleContains", "titlePattern", "className", "requireVisible", "requireTitle"];

    public static readonly string[] RuleProperties =
        ["id", "name", "group", "locator", "appearance", "placement", "inputMode", "focusPolicy", "zOrder", "followTarget", "enabled", "priority", "visibility"];

    public static readonly string[] PlacementProperties = ["mode", "offsetX", "offsetY", "padding", "width", "height", "alignment", "multiRect"];

    public static readonly string[] UiAutomationProperties =
        ["type", "automationId", "name", "nameMatch", "ignoreCase", "controlType", "className", "index", "maxDepth", "maxNodes", "ancestor"];

    /// <summary>Largest accepted absolute coordinate or size, in pixels or DIPs.</summary>
    public const int MaxCoordinate = 1_000_000;

    /// <summary>Upper bound for <see cref="UiAutomationLocator.MaxDepth"/> in profiles.</summary>
    public const int MaxUiaDepth = 256;

    /// <summary>Upper bound for <see cref="UiAutomationLocator.MaxNodes"/> in profiles.</summary>
    public const int MaxUiaNodes = 100_000;

    /// <summary>Upper bound for <see cref="UiAutomationLocator.Index"/>.</summary>
    public const int MaxUiaIndex = 10_000;

    /// <summary>Upper bound for <see cref="OcrTextLocator.MinInterval"/> (one day).</summary>
    public const int MaxMinIntervalMs = 86_400_000;

    /// <summary>Upper bound for a highlight thickness (DIPs).</summary>
    public const double MaxThickness = 1000;

    /// <summary>Maximum length of a rule id or a registered factory id.</summary>
    public const int MaxIdLength = 256;

    /// <summary>Default opacity of a <c>transparent</c> appearance when the document omits it.</summary>
    public const double DefaultTransparentOpacity = 0.5;

    /// <summary>The placement preset a mode starts from; object placements override individual fields.</summary>
    public static OverlayPlacement Preset(PlacementMode mode) => mode switch
    {
        PlacementMode.Right => OverlayPlacement.Right(),
        PlacementMode.Left => OverlayPlacement.Left(),
        PlacementMode.Above => OverlayPlacement.Above(),
        PlacementMode.Below => OverlayPlacement.Below(),
        PlacementMode.Center => OverlayPlacement.Centered(),
        _ => OverlayPlacement.CoverTarget,
    };

    /// <summary>
    /// The <c>fallbackName</c> convention: a <c>uiAutomation</c> locator with <c>fallbackName</c> becomes
    /// <c>FallbackLocator[primary, ByName(fallbackName)]</c>. The fallback keeps the primary's search scope
    /// (ancestor and bounds) and uses the document's <c>nameMatch</c>/<c>ignoreCase</c>.
    /// </summary>
    public static FallbackLocator WithFallbackName(UiAutomationLocator primary, TextQuery fallbackName) =>
        new([primary, FallbackNameLocator(primary, fallbackName)]);

    private static UiAutomationLocator FallbackNameLocator(UiAutomationLocator primary, TextQuery fallbackName) => new()
    {
        Name = fallbackName,
        Ancestor = primary.Ancestor,
        MaxDepth = primary.MaxDepth,
        MaxNodes = primary.MaxNodes,
    };

    /// <summary>Recognizes a chain produced by <see cref="WithFallbackName"/> so it is written back in the short form.</summary>
    public static bool TryMatchFallbackName(FallbackLocator chain, out UiAutomationLocator? primary, out TextQuery? fallbackName)
    {
        primary = null;
        fallbackName = null;
        if (!chain.ContinueOnError || chain.Locators.Count != 2) return false;
        if (chain.Locators[0] is not UiAutomationLocator p || chain.Locators[1] is not UiAutomationLocator f || f.Name is null) return false;
        if (p.IsEmpty) return false;
        // The shared nameMatch/ignoreCase fields must agree when the primary also has a name.
        if (p.Name is not null && (p.Name.Mode != f.Name.Mode || p.Name.IgnoreCase != f.Name.IgnoreCase)) return false;
        if (!f.Name.NormalizeWhitespace) return false;
        if (!Equals(FallbackNameLocator(p, f.Name), f)) return false;
        primary = p;
        fallbackName = f.Name;
        return true;
    }
}
