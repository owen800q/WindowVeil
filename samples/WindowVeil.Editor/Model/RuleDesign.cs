using WindowVeil.Appearance;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WindowVeil.Text;

namespace WindowVeil.Editor.Model;

/// <summary>How the designer describes the target element.</summary>
public enum LocatorMode
{
    /// <summary>A UI Automation selector (AutomationId, Name, ControlType, ClassName).</summary>
    UiAutomation,

    /// <summary>Text exposed through UI Automation (TextPattern ranges or text element names).</summary>
    Text,

    /// <summary>A manual rectangle relative to the target window or its client area (physical pixels).</summary>
    WindowRectangle,

    /// <summary>A locator the designer cannot edit (for example an imported OCR or normalized locator); kept unchanged.</summary>
    Existing,
}

/// <summary>Appearance kinds offered by the designer.</summary>
public enum AppearanceType
{
    /// <summary><see cref="MaskAppearance.Solid(string)"/>.</summary>
    SolidMask,

    /// <summary><see cref="MaskAppearance.Transparent"/>.</summary>
    TransparentMask,

    /// <summary><see cref="HighlightAppearance"/>.</summary>
    Highlight,

    /// <summary><see cref="SpotlightAppearance"/>.</summary>
    Spotlight,

    /// <summary>A WPF item of the editor gallery, referenced as <see cref="RegisteredAppearance"/> so it exports to JSON.</summary>
    GalleryItem,

    /// <summary>Any id registered in the engine's <see cref="AppearanceRegistry"/> (developer-provided factories).</summary>
    RegisteredFactory,
}

/// <summary>
/// The plain-data state of the rule designer. <see cref="RuleBuilder"/> turns it into an <see cref="OverlayRule"/> and
/// back. It has no WPF dependencies so it can be unit tested.
/// </summary>
public sealed record RuleDesign
{
    /// <summary>Rule id (empty = generate one).</summary>
    public string Id { get; init; } = "";

    /// <summary>Optional display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Optional group.</summary>
    public string Group { get; init; } = "";

    /// <summary>Whether the rule is enabled.</summary>
    public bool Enabled { get; init; } = true;

    // ------------------------------------------------------------ locator

    /// <summary>Locator mode.</summary>
    public LocatorMode LocatorMode { get; init; } = LocatorMode.UiAutomation;

    /// <summary>UIA: exact AutomationId (empty = not used).</summary>
    public string AutomationId { get; init; } = "";

    /// <summary>UIA: Name (empty = not used).</summary>
    public string ElementName { get; init; } = "";

    /// <summary>UIA: Name match mode.</summary>
    public TextMatch NameMatch { get; init; } = TextMatch.Exact;

    /// <summary>UIA: whether the Name comparison ignores case.</summary>
    public bool NameIgnoreCase { get; init; }

    /// <summary>UIA: control type (empty = not used).</summary>
    public string ControlType { get; init; } = "";

    /// <summary>UIA: class name (empty = not used).</summary>
    public string ClassName { get; init; } = "";

    /// <summary>UIA: which match to use (0-based).</summary>
    public int Index { get; init; }

    /// <summary>Text: the text to find.</summary>
    public string Text { get; init; } = "";

    /// <summary>Text: match mode.</summary>
    public TextMatch TextMatch { get; init; } = TextMatch.Contains;

    /// <summary>Text: whether matching ignores case.</summary>
    public bool TextIgnoreCase { get; init; } = true;

    /// <summary>Text: optional AutomationId of the element to search within.</summary>
    public string TextScopeAutomationId { get; init; } = "";

    /// <summary>Rectangle (physical pixels relative to <see cref="Anchor"/>).</summary>
    public PixelRect Rectangle { get; init; }

    /// <summary>Rectangle anchor.</summary>
    public WindowAnchor Anchor { get; init; } = WindowAnchor.Client;

    /// <summary>For UIA/Text modes: fall back to <see cref="Rectangle"/> when the element is not found.</summary>
    public bool RectangleFallback { get; init; }

    /// <summary>The locator kept unchanged in <see cref="LocatorMode.Existing"/>.</summary>
    public ElementLocator? ExistingLocator { get; init; }

    // ------------------------------------------------------------ appearance

    /// <summary>Appearance type.</summary>
    public AppearanceType AppearanceType { get; init; } = AppearanceType.SolidMask;

    /// <summary>Color as hex text (#RGB, #RRGGBB or #AARRGGBB).</summary>
    public string Color { get; init; } = "#202020";

    /// <summary>Opacity in 0..1 (transparent masks, highlights, spotlights).</summary>
    public double Opacity { get; init; } = 1.0;

    /// <summary>Highlight border thickness in DIPs.</summary>
    public double Thickness { get; init; } = 3;

    /// <summary>Spotlight scope.</summary>
    public SpotlightScope SpotlightScope { get; init; } = SpotlightScope.TargetWindow;

    /// <summary>Registered factory id (gallery item or developer factory).</summary>
    public string FactoryId { get; init; } = "";

    /// <summary>Registered factory parameters (kept from imported rules).</summary>
    public IReadOnlyDictionary<string, string> FactoryParameters { get; init; } = new Dictionary<string, string>();

    // ------------------------------------------------------------ placement

    /// <summary>Placement mode.</summary>
    public PlacementMode PlacementMode { get; init; } = PlacementMode.Cover;

    /// <summary>Horizontal offset (DIPs).</summary>
    public double OffsetX { get; init; }

    /// <summary>Vertical offset (DIPs).</summary>
    public double OffsetY { get; init; }

    /// <summary>Padding (DIPs).</summary>
    public Insets Padding { get; init; }

    /// <summary>Explicit width (DIPs), or null for the content size.</summary>
    public double? Width { get; init; }

    /// <summary>Explicit height (DIPs), or null for the content size.</summary>
    public double? Height { get; init; }

    /// <summary>Cross-axis alignment.</summary>
    public PlacementAlignment Alignment { get; init; } = PlacementAlignment.Center;

    /// <summary>Multi-rectangle handling.</summary>
    public MultiRectMode MultiRect { get; init; } = MultiRectMode.Each;

    // ------------------------------------------------------------ behavior

    /// <summary>Pointer behavior.</summary>
    public OverlayInputMode InputMode { get; init; } = OverlayInputMode.PassThrough;

    /// <summary>Focus behavior of interactive overlays.</summary>
    public OverlayFocusPolicy FocusPolicy { get; init; } = OverlayFocusPolicy.NoActivate;

    /// <summary>Z-order.</summary>
    public OverlayZOrder ZOrder { get; init; } = OverlayZOrder.AboveTarget;

    /// <summary>Whether the overlay follows the target.</summary>
    public bool FollowTarget { get; init; } = true;

    /// <summary>Visibility policy.</summary>
    public OverlayVisibility Visibility { get; init; } = OverlayVisibility.WhenTargetVisible;

    /// <summary>Priority.</summary>
    public int Priority { get; init; }
}
