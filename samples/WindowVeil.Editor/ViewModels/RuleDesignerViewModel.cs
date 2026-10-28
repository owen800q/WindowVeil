using System.Globalization;
using System.Runtime.CompilerServices;
using WindowVeil.Appearance;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.Mvvm;
using WindowVeil.Editor.Services;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using WindowVeil.Text;
using MediaColor = System.Windows.Media.Color;

namespace WindowVeil.Editor.ViewModels;

/// <summary>
/// The rule designer (locator, appearance, placement, input). A thin binding layer over the plain
/// <see cref="RuleDesign"/> record; <see cref="RuleBuilder"/> does the actual conversion and validation.
/// </summary>
public sealed class RuleDesignerViewModel : ObservableObject
{
    private static readonly string[] DerivedProperties =
    [
        nameof(IsUiaMode), nameof(IsTextMode), nameof(IsRectangleMode), nameof(IsExistingMode), nameof(ShowRectangle), nameof(ShowFallbackOption),
        nameof(ExistingLocatorText), nameof(ShowColor), nameof(ShowOpacity), nameof(ShowThickness), nameof(ShowSpotlightScope),
        nameof(ShowGalleryChoice), nameof(ShowFactoryId), nameof(ShowSize), nameof(ShowAlignment), nameof(ShowFocusPolicy), nameof(OpacityText),
    ];

    private readonly Func<TargetWindowInfo?> _currentWindow;
    private readonly EditorAppearanceCatalog _catalog;
    private readonly Dictionary<string, string> _fieldErrors = new(StringComparer.Ordinal);
    private RuleDesign _d = new() { Id = RuleBuilder.NewId() };
    private string _paddingText = "0";
    private string _widthText = "";
    private string _heightText = "";
    private string _indexText = "0";
    private string _validationText = "";
    private string _preview = "";
    private bool _isValid;
    private string? _editingRuleId;

    /// <summary>Creates the designer.</summary>
    /// <param name="currentWindow">Returns a fresh snapshot of the target window (for anchor conversions), or null.</param>
    /// <param name="catalog">The WPF appearance gallery.</param>
    /// <param name="registeredIds">Returns every id registered in the engine's appearance registry.</param>
    public RuleDesignerViewModel(Func<TargetWindowInfo?> currentWindow, EditorAppearanceCatalog catalog, Func<IReadOnlyCollection<string>> registeredIds)
    {
        _currentWindow = currentWindow ?? throw new ArgumentNullException(nameof(currentWindow));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        RegisteredIds = registeredIds ?? throw new ArgumentNullException(nameof(registeredIds));
        Recompute();
    }

    // ------------------------------------------------------------ option lists

    /// <summary>Locator modes.</summary>
    public static IReadOnlyList<Option<LocatorMode>> LocatorModes { get; } =
    [
        new(LocatorMode.UiAutomation, "UI Automation element"),
        new(LocatorMode.Text, "Text (TextPattern / text elements)"),
        new(LocatorMode.WindowRectangle, "Rectangle in the window"),
        new(LocatorMode.Existing, "Imported locator (read-only)"),
    ];

    /// <summary>Text match modes.</summary>
    public static IReadOnlyList<Option<TextMatch>> TextMatches { get; } =
    [
        new(TextMatch.Exact, "Equals"), new(TextMatch.Contains, "Contains"), new(TextMatch.StartsWith, "Starts with"), new(TextMatch.Regex, "Regex"),
    ];

    /// <summary>Anchors.</summary>
    public static IReadOnlyList<Option<WindowAnchor>> Anchors { get; } =
    [
        new(WindowAnchor.Client, "Client area"), new(WindowAnchor.Window, "Window frame"),
    ];

    /// <summary>Appearance types.</summary>
    public static IReadOnlyList<Option<AppearanceType>> AppearanceTypes { get; } =
    [
        new(AppearanceType.SolidMask, "Solid mask"),
        new(AppearanceType.TransparentMask, "Transparent mask"),
        new(AppearanceType.Highlight, "Highlight border"),
        new(AppearanceType.Spotlight, "Spotlight"),
        new(AppearanceType.GalleryItem, "Built-in WPF gallery item"),
        new(AppearanceType.RegisteredFactory, "Registered factory id"),
    ];

    /// <summary>Spotlight scopes.</summary>
    public static IReadOnlyList<Option<SpotlightScope>> SpotlightScopes { get; } =
    [
        new(SpotlightScope.TargetWindow, "Target window"), new(SpotlightScope.Monitor, "Whole monitor"),
    ];

    /// <summary>Placement modes.</summary>
    public static IReadOnlyList<Option<PlacementMode>> PlacementModes { get; } =
    [
        new(PlacementMode.Cover, "Cover"), new(PlacementMode.Right, "Right"), new(PlacementMode.Left, "Left"),
        new(PlacementMode.Above, "Above"), new(PlacementMode.Below, "Below"), new(PlacementMode.Center, "Center"),
    ];

    /// <summary>Alignments.</summary>
    public static IReadOnlyList<Option<PlacementAlignment>> Alignments { get; } =
    [
        new(PlacementAlignment.Start, "Start"), new(PlacementAlignment.Center, "Center"), new(PlacementAlignment.End, "End"),
    ];

    /// <summary>Multi-rectangle modes.</summary>
    public static IReadOnlyList<Option<MultiRectMode>> MultiRectModes { get; } =
    [
        new(MultiRectMode.Each, "Each rectangle"), new(MultiRectMode.Union, "Union"),
    ];

    /// <summary>Input modes.</summary>
    public static IReadOnlyList<Option<OverlayInputMode>> InputModes { get; } =
    [
        new(OverlayInputMode.PassThrough, "Pass-through (visual only)"), new(OverlayInputMode.Interactive, "Interactive (receives the mouse)"),
    ];

    /// <summary>Focus policies.</summary>
    public static IReadOnlyList<Option<OverlayFocusPolicy>> FocusPolicies { get; } =
    [
        new(OverlayFocusPolicy.NoActivate, "Never activate"), new(OverlayFocusPolicy.ActivateOnClick, "Activate on click (keyboard input)"),
    ];

    /// <summary>Z-orders.</summary>
    public static IReadOnlyList<Option<OverlayZOrder>> ZOrders { get; } =
    [
        new(OverlayZOrder.AboveTarget, "Above the target window"), new(OverlayZOrder.Topmost, "Topmost (above every window)"),
    ];

    /// <summary>Visibility policies.</summary>
    public static IReadOnlyList<Option<OverlayVisibility>> Visibilities { get; } =
    [
        new(OverlayVisibility.WhenTargetVisible, "While the target is visible"), new(OverlayVisibility.WhenTargetForeground, "Only while the target is in the foreground"),
    ];

    /// <summary>Gallery WPF items (for the gallery choice combo box).</summary>
    public IEnumerable<CatalogEntry> GalleryEntries => _catalog.Entries;

    /// <summary>Every registered factory id.</summary>
    public Func<IReadOnlyCollection<string>> RegisteredIds { get; }

    /// <summary>Registered ids for the factory combo box.</summary>
    public IReadOnlyList<string> RegisteredIdList => RegisteredIds().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    // ------------------------------------------------------------ identity

    /// <summary>Id of the rule being edited (null = a new rule).</summary>
    public string? EditingRuleId { get => _editingRuleId; private set { if (Set(ref _editingRuleId, value)) OnPropertyChanged(nameof(EditingText)); } }

    /// <summary>Editing status text.</summary>
    public string EditingText => _editingRuleId is null ? "New rule" : $"Editing '{_editingRuleId}'";

    /// <summary>Rule id.</summary>
    public string RuleId { get => _d.Id; set => Update(_d with { Id = value ?? "" }); }

    /// <summary>Rule name.</summary>
    public string RuleName { get => _d.Name; set => Update(_d with { Name = value ?? "" }); }

    /// <summary>Group.</summary>
    public string Group { get => _d.Group; set => Update(_d with { Group = value ?? "" }); }

    // ------------------------------------------------------------ locator

    /// <summary>Locator mode.</summary>
    public LocatorMode LocatorMode
    {
        get => _d.LocatorMode;
        set
        {
            if (value == LocatorMode.Existing && _d.ExistingLocator is null) { OnPropertyChanged(); return; }
            Update(_d with { LocatorMode = value });
        }
    }

    /// <summary>UIA AutomationId.</summary>
    public string AutomationId { get => _d.AutomationId; set => Update(_d with { AutomationId = value ?? "" }); }

    /// <summary>UIA Name.</summary>
    public string ElementName { get => _d.ElementName; set => Update(_d with { ElementName = value ?? "" }); }

    /// <summary>UIA Name match.</summary>
    public TextMatch NameMatch { get => _d.NameMatch; set => Update(_d with { NameMatch = value }); }

    /// <summary>UIA Name ignore case.</summary>
    public bool NameIgnoreCase { get => _d.NameIgnoreCase; set => Update(_d with { NameIgnoreCase = value }); }

    /// <summary>UIA ControlType.</summary>
    public string ControlType { get => _d.ControlType; set => Update(_d with { ControlType = value ?? "" }); }

    /// <summary>UIA ClassName.</summary>
    public string ClassName { get => _d.ClassName; set => Update(_d with { ClassName = value ?? "" }); }

    /// <summary>UIA match index as text.</summary>
    public string IndexText
    {
        get => _indexText;
        set
        {
            _indexText = value ?? "";
            if (int.TryParse(_indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0) { _fieldErrors.Remove(nameof(IndexText)); Update(_d with { Index = i }); }
            else { _fieldErrors[nameof(IndexText)] = "The match index must be a non-negative integer."; OnPropertyChanged(); Recompute(); }
        }
    }

    /// <summary>Text to locate.</summary>
    public string Text { get => _d.Text; set => Update(_d with { Text = value ?? "" }); }

    /// <summary>Text match mode.</summary>
    public TextMatch TextMatch { get => _d.TextMatch; set => Update(_d with { TextMatch = value }); }

    /// <summary>Text ignore case.</summary>
    public bool TextIgnoreCase { get => _d.TextIgnoreCase; set => Update(_d with { TextIgnoreCase = value }); }

    /// <summary>Optional AutomationId scope for the text search.</summary>
    public string TextScopeAutomationId { get => _d.TextScopeAutomationId; set => Update(_d with { TextScopeAutomationId = value ?? "" }); }

    /// <summary>Rectangle X (physical px).</summary>
    public int RectX { get => _d.Rectangle.X; set => Update(_d with { Rectangle = _d.Rectangle with { X = value } }); }

    /// <summary>Rectangle Y (physical px).</summary>
    public int RectY { get => _d.Rectangle.Y; set => Update(_d with { Rectangle = _d.Rectangle with { Y = value } }); }

    /// <summary>Rectangle width (physical px).</summary>
    public int RectWidth { get => _d.Rectangle.Width; set => Update(_d with { Rectangle = _d.Rectangle with { Width = value } }); }

    /// <summary>Rectangle height (physical px).</summary>
    public int RectHeight { get => _d.Rectangle.Height; set => Update(_d with { Rectangle = _d.Rectangle with { Height = value } }); }

    /// <summary>Rectangle anchor. Changing it re-expresses the rectangle for the new origin when the target is known.</summary>
    public WindowAnchor Anchor
    {
        get => _d.Anchor;
        set
        {
            if (value == _d.Anchor) return;
            var rect = _d.Rectangle;
            if (_currentWindow() is { } w && !rect.IsEmpty)
                rect = ScreenGeometry.ChangeAnchor(rect, _d.Anchor, value, w.Bounds, w.ClientBounds);
            Update(_d with { Anchor = value, Rectangle = rect });
            OnPropertiesChanged(nameof(RectX), nameof(RectY), nameof(RectWidth), nameof(RectHeight));
        }
    }

    /// <summary>Whether a UIA/Text locator falls back to the rectangle.</summary>
    public bool RectangleFallback { get => _d.RectangleFallback; set => Update(_d with { RectangleFallback = value }); }

    /// <summary>Description of the imported locator.</summary>
    public string ExistingLocatorText => _d.ExistingLocator is null ? "" : LocatorFormatter.Summary(_d.ExistingLocator);

    /// <summary>UI flags.</summary>
    public bool IsUiaMode => _d.LocatorMode == LocatorMode.UiAutomation;

    /// <summary>UI flags.</summary>
    public bool IsTextMode => _d.LocatorMode == LocatorMode.Text;

    /// <summary>UI flags.</summary>
    public bool IsRectangleMode => _d.LocatorMode == LocatorMode.WindowRectangle;

    /// <summary>UI flags.</summary>
    public bool IsExistingMode => _d.LocatorMode == LocatorMode.Existing;

    /// <summary>UI flags.</summary>
    public bool ShowFallbackOption => IsUiaMode || IsTextMode;

    /// <summary>UI flags.</summary>
    public bool ShowRectangle => IsRectangleMode || (ShowFallbackOption && _d.RectangleFallback);

    // ------------------------------------------------------------ appearance

    /// <summary>Appearance type.</summary>
    public AppearanceType AppearanceType
    {
        get => _d.AppearanceType;
        set
        {
            if (value == _d.AppearanceType) return;
            var d = _d with { AppearanceType = value };
            if (value == AppearanceType.TransparentMask && d.Opacity >= 1) d = d with { Opacity = 0.5 };
            if (value == AppearanceType.Spotlight && d.Opacity >= 1) d = d with { Opacity = 0.6, Color = "#000000" };
            if (value == AppearanceType.Highlight && d.Color == "#202020") d = d with { Color = "#FFB300" };
            if (value == AppearanceType.GalleryItem && !_catalog.Contains(d.FactoryId)) d = d with { FactoryId = _catalog.Entries.FirstOrDefault()?.Id ?? "" };
            Update(d);
            OnPropertiesChanged(nameof(ColorText), nameof(Opacity), nameof(FactoryId), nameof(IsColorValid), nameof(ColorPreview));
        }
    }

    /// <summary>Color as hex text.</summary>
    public string ColorText
    {
        get => _d.Color;
        set
        {
            Update(_d with { Color = value ?? "" });
            OnPropertiesChanged(nameof(IsColorValid), nameof(ColorPreview));
        }
    }

    /// <summary>Whether <see cref="ColorText"/> parses (OverlayColor.TryParse).</summary>
    public bool IsColorValid => OverlayColor.TryParse(_d.Color, out _);

    /// <summary>Swatch color (transparent when invalid).</summary>
    public MediaColor ColorPreview => OverlayColor.TryParse(_d.Color, out var c) ? MediaColor.FromArgb(c.A, c.R, c.G, c.B) : MediaColor.FromArgb(0, 0, 0, 0);

    /// <summary>Opacity 0..1.</summary>
    public double Opacity
    {
        get => _d.Opacity;
        set { Update(_d with { Opacity = Math.Clamp(Math.Round(value, 2), 0, 1) }); OnPropertyChanged(nameof(OpacityText)); }
    }

    /// <summary>Opacity as a percentage.</summary>
    public string OpacityText => (_d.Opacity * 100).ToString("0", CultureInfo.InvariantCulture) + " %";

    /// <summary>Highlight thickness (DIPs).</summary>
    public double Thickness { get => _d.Thickness; set => Update(_d with { Thickness = value }); }

    /// <summary>Spotlight scope.</summary>
    public SpotlightScope SpotlightScope { get => _d.SpotlightScope; set => Update(_d with { SpotlightScope = value }); }

    /// <summary>Registered factory id (gallery item or free id).</summary>
    public string FactoryId
    {
        get => _d.FactoryId;
        set
        {
            // null comes from a combo box whose items do not contain the id; equal ids keep imported parameters.
            if (value is null || value == _d.FactoryId) return;
            Update(_d with { FactoryId = value, FactoryParameters = new Dictionary<string, string>() });
        }
    }

    /// <summary>UI flags.</summary>
    public bool ShowColor => _d.AppearanceType is AppearanceType.SolidMask or AppearanceType.TransparentMask or AppearanceType.Highlight or AppearanceType.Spotlight;

    /// <summary>UI flags.</summary>
    public bool ShowOpacity => _d.AppearanceType is AppearanceType.TransparentMask or AppearanceType.Highlight or AppearanceType.Spotlight;

    /// <summary>UI flags.</summary>
    public bool ShowThickness => _d.AppearanceType == AppearanceType.Highlight;

    /// <summary>UI flags.</summary>
    public bool ShowSpotlightScope => _d.AppearanceType == AppearanceType.Spotlight;

    /// <summary>UI flags.</summary>
    public bool ShowGalleryChoice => _d.AppearanceType == AppearanceType.GalleryItem;

    /// <summary>UI flags.</summary>
    public bool ShowFactoryId => _d.AppearanceType == AppearanceType.RegisteredFactory;

    // ------------------------------------------------------------ placement

    /// <summary>Placement mode.</summary>
    public PlacementMode PlacementMode
    {
        get => _d.PlacementMode;
        set
        {
            if (value == _d.PlacementMode) return;
            // Mirror the OverlayPlacement factory defaults: adjacent placements use the union of multi-line targets.
            var d = _d with { PlacementMode = value, MultiRect = value == PlacementMode.Cover ? MultiRectMode.Each : MultiRectMode.Union };
            if (value is PlacementMode.Above or PlacementMode.Below && _d.PlacementMode is not (PlacementMode.Above or PlacementMode.Below))
                d = d with { Alignment = PlacementAlignment.Start };
            Update(d);
            OnPropertiesChanged(nameof(MultiRect), nameof(Alignment));
        }
    }

    /// <summary>Offset X (DIPs).</summary>
    public double OffsetX { get => _d.OffsetX; set => Update(_d with { OffsetX = value }); }

    /// <summary>Offset Y (DIPs).</summary>
    public double OffsetY { get => _d.OffsetY; set => Update(_d with { OffsetY = value }); }

    /// <summary>Padding text: "4", "4, 2" or "4, 2, 4, 2" (DIPs).</summary>
    public string PaddingText
    {
        get => _paddingText;
        set
        {
            _paddingText = value ?? "";
            if (RuleBuilder.TryParseInsets(_paddingText, out var insets)) { _fieldErrors.Remove(nameof(PaddingText)); Update(_d with { Padding = insets }); }
            else { _fieldErrors[nameof(PaddingText)] = "Padding must be one, two or four numbers (DIP)."; OnPropertyChanged(); Recompute(); }
        }
    }

    /// <summary>Width text (empty = content size).</summary>
    public string WidthText { get => _widthText; set => SetOptionalSize(ref _widthText, value, isWidth: true); }

    /// <summary>Height text (empty = content size).</summary>
    public string HeightText { get => _heightText; set => SetOptionalSize(ref _heightText, value, isWidth: false); }

    /// <summary>Alignment.</summary>
    public PlacementAlignment Alignment { get => _d.Alignment; set => Update(_d with { Alignment = value }); }

    /// <summary>Multi-rectangle mode.</summary>
    public MultiRectMode MultiRect { get => _d.MultiRect; set => Update(_d with { MultiRect = value }); }

    /// <summary>UI flags.</summary>
    public bool ShowSize => _d.PlacementMode != PlacementMode.Cover;

    /// <summary>UI flags.</summary>
    public bool ShowAlignment => _d.PlacementMode is PlacementMode.Right or PlacementMode.Left or PlacementMode.Above or PlacementMode.Below;

    // ------------------------------------------------------------ behavior

    /// <summary>Input mode.</summary>
    public OverlayInputMode InputMode { get => _d.InputMode; set => Update(_d with { InputMode = value }); }

    /// <summary>Focus policy.</summary>
    public OverlayFocusPolicy FocusPolicy { get => _d.FocusPolicy; set => Update(_d with { FocusPolicy = value }); }

    /// <summary>UI flags.</summary>
    public bool ShowFocusPolicy => _d.InputMode == OverlayInputMode.Interactive;

    /// <summary>Z-order.</summary>
    public OverlayZOrder ZOrder { get => _d.ZOrder; set => Update(_d with { ZOrder = value }); }

    /// <summary>Follow target.</summary>
    public bool FollowTarget { get => _d.FollowTarget; set => Update(_d with { FollowTarget = value }); }

    /// <summary>Visibility.</summary>
    public OverlayVisibility Visibility { get => _d.Visibility; set => Update(_d with { Visibility = value }); }

    /// <summary>Priority.</summary>
    public int Priority { get => _d.Priority; set => Update(_d with { Priority = value }); }

    // ------------------------------------------------------------ results

    /// <summary>Validation messages (empty when valid).</summary>
    public string ValidationText { get => _validationText; private set => Set(ref _validationText, value); }

    /// <summary>Description of the rule that would be built.</summary>
    public string PreviewText { get => _preview; private set => Set(ref _preview, value); }

    /// <summary>Whether the design builds.</summary>
    public bool IsValid { get => _isValid; private set => Set(ref _isValid, value); }

    /// <summary>Current design (plain data).</summary>
    public RuleDesign Design => _d;

    /// <summary>Builds the rule; null result with errors when the design is invalid.</summary>
    public RuleBuildResult Build()
    {
        if (_fieldErrors.Count > 0) return new RuleBuildResult(null, _fieldErrors.Values.ToArray());
        return RuleBuilder.TryBuild(_d);
    }

    /// <summary>Loads a rule for editing.</summary>
    public void LoadRule(OverlayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Load(RuleBuilder.FromRule(rule, _catalog.Contains));
        EditingRuleId = rule.Id;
    }

    /// <summary>Starts a new rule, keeping the current locator so "pick → add" stays quick.</summary>
    public void NewRule()
    {
        Load(_d with { Id = RuleBuilder.NewId(), Name = "", Enabled = true });
        EditingRuleId = null;
    }

    /// <summary>Marks the designer as editing <paramref name="ruleId"/> (after "Add").</summary>
    public void MarkEditing(string ruleId) => EditingRuleId = ruleId;

    /// <summary>Applies a locator ("Use as target").</summary>
    public void UseLocator(ElementLocator locator)
    {
        var d = RuleBuilder.WithLocator(_d, locator);
        if (d.LocatorMode == LocatorMode.WindowRectangle && _d.LocatorMode is LocatorMode.UiAutomation or LocatorMode.Text && _d.RectangleFallback)
        {
            // Keep the element locator and only refresh its rectangle fallback.
            d = _d with { Rectangle = d.Rectangle, Anchor = d.Anchor };
        }
        Load(d);
    }

    /// <summary>Applies a manual rectangle (window-relative physical pixels for <paramref name="anchor"/>).</summary>
    public void UseRectangle(PixelRect relative, WindowAnchor anchor)
    {
        var d = _d with { Rectangle = relative, Anchor = anchor };
        if (!(d.LocatorMode is LocatorMode.UiAutomation or LocatorMode.Text && d.RectangleFallback)) d = d with { LocatorMode = LocatorMode.WindowRectangle };
        Load(d);
    }

    /// <summary>Applies a gallery tile.</summary>
    public void UseGalleryItem(GalleryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var d = _d with { AppearanceType = item.AppearanceType };
        if (item.Entry is { } entry)
        {
            d = d with { FactoryId = entry.Id, FactoryParameters = new Dictionary<string, string>(), InputMode = entry.SuggestedInputMode };
            if (entry.SuggestedPlacement is { } p)
            {
                d = d with
                {
                    PlacementMode = p.Mode, OffsetX = p.OffsetX, OffsetY = p.OffsetY, Padding = p.Padding, Width = p.Width, Height = p.Height,
                    Alignment = p.Alignment, MultiRect = p.MultiRect,
                };
            }
        }
        else
        {
            d = item.AppearanceType switch
            {
                AppearanceType.SolidMask => d with { Color = "#202020", Opacity = 1, PlacementMode = PlacementMode.Cover, MultiRect = MultiRectMode.Each },
                AppearanceType.TransparentMask => d with { Color = "#E53935", Opacity = 0.4, PlacementMode = PlacementMode.Cover, MultiRect = MultiRectMode.Each },
                AppearanceType.Highlight => d with { Color = "#FFB300", Opacity = 1, Thickness = 3, PlacementMode = PlacementMode.Cover, MultiRect = MultiRectMode.Each },
                _ => d,
            };
        }
        Load(d);
    }

    private void Load(RuleDesign design)
    {
        _d = design;
        _fieldErrors.Clear();
        _paddingText = RuleBuilder.FormatInsets(design.Padding);
        _widthText = design.Width?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        _heightText = design.Height?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        _indexText = design.Index.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(string.Empty); // refresh every binding
        Recompute();
    }

    private void SetOptionalSize(ref string field, string? value, bool isWidth, [CallerMemberName] string? name = null)
    {
        field = value ?? "";
        string key = name!;
        double? parsed = null;
        if (!string.IsNullOrWhiteSpace(field))
        {
            if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v > 0) parsed = v;
            else { _fieldErrors[key] = $"{(isWidth ? "Width" : "Height")} must be a positive number (DIP) or empty."; OnPropertyChanged(name); Recompute(); return; }
        }
        _fieldErrors.Remove(key);
        Update(isWidth ? _d with { Width = parsed } : _d with { Height = parsed }, name);
    }

    private void Update(RuleDesign design, [CallerMemberName] string? name = null)
    {
        bool changed = design != _d;
        _d = design;
        OnPropertyChanged(name);
        if (!changed) return;
        OnPropertiesChanged(DerivedProperties);
        Recompute();
    }

    private void Recompute()
    {
        var result = Build();
        IsValid = result.Succeeded;
        ValidationText = string.Join(Environment.NewLine, result.Errors);
        PreviewText = result.Rule is { } r
            ? $"{LocatorFormatter.Summary(r.Target)}\n{LocatorFormatter.DescribeAppearance(r.Appearance)} · {r.Placement.Mode} · {r.InputMode}"
            : "";
    }
}
