using WindowVeil.Geometry;
using WindowVeil.Imaging;

namespace WindowVeil.Appearance;

/// <summary>
/// What an overlay looks like. Core defines the native-renderable appearances; other packages add more
/// (for example <c>WpfAppearance</c> in <c>WindowVeil.Wpf</c>). Renderers declare which appearances they support.
/// </summary>
public abstract record OverlayAppearance
{
    /// <summary>Stable kind identifier, e.g. <c>solid</c> or <c>wpf</c>.</summary>
    public abstract string Kind { get; }
}

/// <summary>A filled rectangle covering the target: opaque (<see cref="Solid(string)"/>) or translucent (<see cref="Transparent"/>).</summary>
public sealed record MaskAppearance : OverlayAppearance
{
    /// <summary>Creates a mask.</summary>
    public MaskAppearance(OverlayColor color, double opacity = 1.0)
    {
        if (double.IsNaN(opacity) || opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be in 0..1.");
        Color = color.WithAlpha(255);
        Opacity = opacity;
    }

    /// <summary>Fill color (alpha is carried by <see cref="Opacity"/>).</summary>
    public OverlayColor Color { get; init; }

    /// <summary>Window opacity in 0..1.</summary>
    public double Opacity { get; init; }

    /// <inheritdoc />
    public override string Kind => Opacity >= 1 ? "solid" : "transparent";

    /// <summary>An opaque mask, e.g. <c>MaskAppearance.Solid("#202020")</c>.</summary>
    public static MaskAppearance Solid(string color) => new(OverlayColor.Parse(color));

    /// <summary>An opaque mask.</summary>
    public static MaskAppearance Solid(OverlayColor color) => new(color);

    /// <summary>A translucent mask.</summary>
    public static MaskAppearance Transparent(string color, double opacity) => new(OverlayColor.Parse(color), opacity);
}

/// <summary>A rectangular border drawn around the target; the inside stays visible and click-through.</summary>
public sealed record HighlightAppearance : OverlayAppearance
{
    /// <summary>Creates a highlight.</summary>
    public HighlightAppearance(OverlayColor color, double thickness = 3, double opacity = 1.0)
    {
        if (thickness <= 0 || double.IsNaN(thickness)) throw new ArgumentOutOfRangeException(nameof(thickness), thickness, "Thickness must be positive.");
        if (double.IsNaN(opacity) || opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be in 0..1.");
        Color = color.WithAlpha(255); Thickness = thickness; Opacity = opacity;
    }

    /// <summary>Border color.</summary>
    public OverlayColor Color { get; init; }

    /// <summary>Border thickness in DIPs.</summary>
    public double Thickness { get; init; }

    /// <summary>Opacity in 0..1.</summary>
    public double Opacity { get; init; }

    /// <inheritdoc />
    public override string Kind => "highlight";

    /// <summary>Creates a highlight from a hex color.</summary>
    public static HighlightAppearance Create(string color, double thickness = 3, double opacity = 1.0) =>
        new(OverlayColor.Parse(color), thickness, opacity);
}

/// <summary>Where a spotlight darkens.</summary>
public enum SpotlightScope
{
    /// <summary>Only the target window is darkened (default; unrelated applications are untouched).</summary>
    TargetWindow,

    /// <summary>The whole monitor hosting the target is darkened.</summary>
    Monitor,
}

/// <summary>Darkens everything around the target, leaving the target itself unobscured.</summary>
public sealed record SpotlightAppearance : OverlayAppearance
{
    /// <summary>Creates a spotlight.</summary>
    public SpotlightAppearance(OverlayColor dimColor, double opacity = 0.6, SpotlightScope scope = SpotlightScope.TargetWindow)
    {
        if (double.IsNaN(opacity) || opacity < 0 || opacity > 1) throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "Opacity must be in 0..1.");
        DimColor = dimColor.WithAlpha(255); Opacity = opacity; Scope = scope;
    }

    /// <summary>Color of the dimmed area.</summary>
    public OverlayColor DimColor { get; init; }

    /// <summary>Opacity of the dimmed area in 0..1.</summary>
    public double Opacity { get; init; }

    /// <summary>Area that is darkened.</summary>
    public SpotlightScope Scope { get; init; }

    /// <inheritdoc />
    public override string Kind => "spotlight";

    /// <summary>A black spotlight with the given opacity.</summary>
    public static SpotlightAppearance Create(double opacity = 0.6, SpotlightScope scope = SpotlightScope.TargetWindow) =>
        new(OverlayColor.Black, opacity, scope);
}

/// <summary>
/// A reference to an appearance factory registered in an <see cref="AppearanceRegistry"/> by id.
/// Profiles use this so JSON never names .NET types. The session resolves it when the rule is added.
/// </summary>
public sealed record RegisteredAppearance(string FactoryId) : OverlayAppearance
{
    /// <summary>String parameters passed to the factory.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <inheritdoc />
    public override string Kind => "registered";

    /// <inheritdoc />
    public bool Equals(RegisteredAppearance? other) =>
        other is not null && FactoryId == other.FactoryId && Parameters.Count == other.Parameters.Count &&
        Parameters.All(kv => other.Parameters.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FactoryId, Parameters.Count);
}
