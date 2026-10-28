using WindowVeil.Appearance;
using WindowVeil.Locators;
using WindowVeil.Rendering;

namespace WindowVeil;

/// <summary>When an overlay is visible.</summary>
public enum OverlayVisibility
{
    /// <summary>While the target window is visible and not minimized (default).</summary>
    WhenTargetVisible,

    /// <summary>Only while the target window is the foreground window.</summary>
    WhenTargetForeground,
}

/// <summary>
/// A declarative overlay: which element (<see cref="Target"/>), what it looks like (<see cref="Appearance"/>),
/// where it goes (<see cref="Placement"/>) and how it behaves. Rules are immutable; update them with
/// <c>with</c> expressions and <see cref="OverlaySession.UpdateRule"/>.
/// </summary>
public sealed record OverlayRule
{
    /// <summary>Unique id within a session. Defaults to a new GUID.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    /// <summary>Optional display name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional group, so related rules can be enabled or disabled together.</summary>
    public string? Group { get; init; }

    /// <summary>What to attach to.</summary>
    public required ElementLocator Target { get; init; }

    /// <summary>How the overlay looks.</summary>
    public required OverlayAppearance Appearance { get; init; }

    /// <summary>Where the overlay goes relative to the target. Default: cover the target.</summary>
    public OverlayPlacement Placement { get; init; } = OverlayPlacement.CoverTarget;

    /// <summary>Pointer behavior. Default pass-through.</summary>
    public OverlayInputMode InputMode { get; init; } = OverlayInputMode.PassThrough;

    /// <summary>Focus behavior for interactive overlays.</summary>
    public OverlayFocusPolicy FocusPolicy { get; init; } = OverlayFocusPolicy.NoActivate;

    /// <summary>Z-order strategy.</summary>
    public OverlayZOrder ZOrder { get; init; } = OverlayZOrder.AboveTarget;

    /// <summary>
    /// Whether the overlay keeps following the element as the window moves and the layout changes (default).
    /// When false, the target is resolved once and the overlay stays at that screen position.
    /// </summary>
    public bool FollowTarget { get; init; } = true;

    /// <summary>Whether the rule is active.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Higher priority rules are resolved and stacked first. Default 0.</summary>
    public int Priority { get; init; }

    /// <summary>Visibility policy.</summary>
    public OverlayVisibility Visibility { get; init; } = OverlayVisibility.WhenTargetVisible;

    /// <summary>
    /// Upper bound for one resolution of this rule. When null, <see cref="SessionOptions.ResolveTimeout"/> applies, or
    /// <see cref="SessionOptions.OcrResolveTimeout"/> when the locator uses OCR.
    /// </summary>
    public TimeSpan? ResolveTimeout { get; init; }

    /// <summary>Validates the rule and throws <see cref="WindowVeilException"/> with <see cref="WindowVeilErrorCode.InvalidRule"/>.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, "Rule id cannot be empty.");
        if (Target is null) throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has no target locator.");
        if (Appearance is null) throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has no appearance.");
        if (Placement is null) throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has no placement.");
        if (Target is UiAutomationLocator { IsEmpty: true })
            throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has a UI Automation locator without conditions.");
        if (Target is UiAutomationLocator { MaxDepth: <= 0 } or UiAutomationLocator { MaxNodes: <= 0 })
            throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has non-positive UI Automation search bounds.");
        if (Target is OcrTextLocator { MinConfidence: < 0 or > 1 })
            throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has an OCR confidence outside 0..1.");
        if (ResolveTimeout is { } t && t <= TimeSpan.Zero)
            throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has a non-positive resolve timeout.");
        if (Placement.Width is <= 0 || Placement.Height is <= 0)
            throw new WindowVeilException(WindowVeilErrorCode.InvalidRule, $"Rule '{Id}' has a non-positive placement size.");
    }
}

/// <summary>Behavior presets for <see cref="MaskRule"/>.</summary>
public sealed record MaskBehavior
{
    /// <summary>Follow the element as it moves (default).</summary>
    public static MaskBehavior FollowTarget { get; } = new() { Follow = true };

    /// <summary>Resolve once and stay at that screen position.</summary>
    public static MaskBehavior Static { get; } = new() { Follow = false };

    /// <summary>Whether the mask follows the target.</summary>
    public bool Follow { get; init; } = true;

    /// <summary>Pointer behavior. Masks are pass-through by default; <see cref="OverlayInputMode.Interactive"/> blocks clicks on the masked control.</summary>
    public OverlayInputMode InputMode { get; init; } = OverlayInputMode.PassThrough;

    /// <summary>Returns a behavior that also blocks clicks on the masked control.</summary>
    public MaskBehavior BlockingInput() => this with { InputMode = OverlayInputMode.Interactive };
}

/// <summary>Convenience factory for mask rules.</summary>
public static class MaskRule
{
    /// <summary>Creates a rule that covers the located element.</summary>
    public static OverlayRule Create(ElementLocator locator, OverlayAppearance appearance, MaskBehavior? behavior = null, string? id = null)
    {
        behavior ??= MaskBehavior.FollowTarget;
        return new OverlayRule
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Target = locator,
            Appearance = appearance,
            FollowTarget = behavior.Follow,
            InputMode = behavior.InputMode,
        };
    }
}
