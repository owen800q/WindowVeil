using WindowVeil.Appearance;
using WindowVeil.Demo.Overlays;
using WindowVeil.Geometry;
using WindowVeil.Locators;
using WindowVeil.Rendering;
using WindowVeil.Text;
using WindowVeil.Wpf;

namespace WindowVeil.Demo;

/// <summary>One toggleable demo overlay.</summary>
public sealed class DemoScenario : ObservableObject
{
    private bool _enabled;
    private string _status = "off";

    /// <summary>Creates a scenario.</summary>
    public DemoScenario(string id, string title, string description, Func<OverlayRule> createRule, bool requiresOcr = false)
    {
        Id = id; Title = title; Description = description; CreateRule = createRule; RequiresOcr = requiresOcr;
    }

    /// <summary>Rule id.</summary>
    public string Id { get; }

    /// <summary>Display title.</summary>
    public string Title { get; }

    /// <summary>What the scenario demonstrates.</summary>
    public string Description { get; }

    /// <summary>Builds the rule (called on the UI thread; WPF factories run later on the overlay dispatcher).</summary>
    public Func<OverlayRule> CreateRule { get; }

    /// <summary>Whether PP-OCRv6 models are needed.</summary>
    public bool RequiresOcr { get; }

    /// <summary>Whether the rule is enabled.</summary>
    public bool IsEnabled { get => _enabled; set { if (Set(ref _enabled, value)) EnabledChanged?.Invoke(this, EventArgs.Empty); } }

    /// <summary>Latest rule status text.</summary>
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Raised when <see cref="IsEnabled"/> changes.</summary>
    public event EventHandler? EnabledChanged;
}

/// <summary>The scenarios shown by the demo. Element ids refer to WindowVeil.DemoTarget.</summary>
public static class DemoScenarios
{
    /// <summary>Creates the scenario list. <paramref name="toolbarCommands"/> are bound to the interactive toolbar.</summary>
    public static IReadOnlyList<DemoScenario> Create(params (string Caption, System.Windows.Input.ICommand Command)[] toolbarCommands) =>
    [
        new("api-key-mask", "Solid mask: API key", "Native opaque rectangle over the API key (UI Automation AutomationId).",
            () => MaskRule.Create(ElementLocator.ByAutomationId("ApiKeyValue"), MaskAppearance.Solid("#202020"), id: "api-key-mask")),
        new("balance-tint", "Transparent mask: balance", "Translucent native mask over the account balance value.",
            () => MaskRule.Create(ElementLocator.ByAutomationId("AccountBalanceValue"), MaskAppearance.Transparent("#3A7BD5", 0.85), id: "balance-tint")),
        new("startup-highlight", "Highlight: startup checkbox", "Native border around a checkbox; the control stays fully usable.",
            () => new OverlayRule { Id = "startup-highlight", Target = ElementLocator.ByAutomationId("StartupCheck"), Appearance = HighlightAppearance.Create("#FFB300", 3), Placement = OverlayPlacement.Cover(3) }),
        new("apply-spotlight", "Spotlight: first Apply button", "Darkens the target window except the selected button (Name match + index).",
            () => new OverlayRule { Id = "apply-spotlight", Target = ElementLocator.ByName("Apply").WithControlType("Button").At(0), Appearance = SpotlightAppearance.Create(0.55), Placement = OverlayPlacement.Cover(4) }),
        new("notes-secret", "Text range mask: \"secret token\"", "TextPattern ranges inside a TextBox; a match spanning two lines yields two rectangles.",
            () => MaskRule.Create(ElementLocator.ByText("secret token"), MaskAppearance.Transparent("#E53935", 0.9), id: "notes-secret")),
        new("balance-badge", "WPF badge next to a label", "A WPF control with data binding and animation, placed 12 DIP to the right of the label.",
            () => new OverlayRule
            {
                Id = "balance-badge", Target = ElementLocator.ByAutomationId("AccountBalanceLabel"),
                Appearance = WpfAppearance.Create(() => new PrivacyBadge("Masked by policy")),
                Placement = OverlayPlacement.Right(offsetX: 160, offsetY: 0),
            }),
        new("toolbar", "Interactive WPF toolbar", "Clickable WPF buttons added to the external window. Clicks are handled by the demo, not forwarded.",
            () => new OverlayRule
            {
                Id = "toolbar", Target = ElementLocator.Window(WindowAnchor.Client),
                Appearance = WpfAppearance.Create(() => new FloatingToolbar(toolbarCommands)),
                Placement = new OverlayPlacement { Mode = PlacementMode.Above, Alignment = PlacementAlignment.End, OffsetX = -8, OffsetY = 44, MultiRect = MultiRectMode.Union },
                InputMode = OverlayInputMode.Interactive,
            }),
        new("ocr-balance", "OCR mask: \"$12,345.67\"", "PP-OCRv6 finds the amount in a capture of the window (no UI Automation).",
            () => MaskRule.Create(ElementLocator.ByOcrText("12,345.67", TextMatch.Contains) with { Expand = new Insets(4, 2, 2, 2) }, MaskAppearance.Solid("#5E35B1"), id: "ocr-balance"),
            requiresOcr: true),
    ];
}
