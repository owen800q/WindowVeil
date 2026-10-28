using System.Windows;
using System.Windows.Media;
using WindowVeil.Appearance;

namespace WindowVeil.Wpf;

/// <summary>
/// An overlay whose content is an arbitrary WPF <see cref="UIElement"/>: a control, a <c>UserControl</c>, a custom
/// XAML control, a panel tree with bindings, commands, templates and animations. Rendered by
/// <see cref="WpfOverlayRenderer"/> (register it with <see cref="WpfWindowVeilBuilderExtensions.UseWpfOverlays"/>).
/// </summary>
/// <remarks>
/// <para>
/// The appearance stores a <b>factory</b>, never an element: WPF elements are bound to the dispatcher thread that
/// created them, so the renderer invokes the factory on the dispatcher thread that owns the overlay host window.
/// The factory must therefore create a <b>new</b> element on every call and must not capture elements, brushes or
/// other <see cref="System.Windows.Threading.DispatcherObject"/>s created on another thread (frozen freezables and plain
/// view models are fine).
/// </para>
/// <para>
/// Record equality compares the factory delegate (target and method), so two appearances built from the same lambda
/// instance are equal, while re-creating the lambda produces a different appearance and recreates the overlay when a
/// rule is updated.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// Appearance = WpfAppearance.Create(() =&gt; new Button { Content = "Custom Action", Width = 140, Height = 36 })
/// Appearance = WpfAppearance.Create(ctx =&gt; new MyBadge { DataContext = new BadgeViewModel(ctx) })
/// Appearance = WpfAppearance.Create&lt;MyCustomUserControl&gt;()
/// </code>
/// </example>
public sealed record WpfAppearance : OverlayAppearance
{
    private readonly double _opacity = 1.0;

    /// <summary>Creates an appearance from a factory that receives the overlay context.</summary>
    /// <param name="factory">Creates the overlay content. Invoked on the renderer's dispatcher thread, once per overlay instance.</param>
    public WpfAppearance(Func<WpfOverlayContext, UIElement> factory)
    {
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <inheritdoc />
    public override string Kind => "wpf";

    /// <summary>Creates the overlay content. Invoked on the renderer's dispatcher thread.</summary>
    public Func<WpfOverlayContext, UIElement> Factory { get; init; }

    /// <summary>
    /// Background painted behind the content over the whole overlay bounds. Default fully transparent: transparent pixels
    /// are not drawn and, for interactive overlays, let clicks through to the application underneath.
    /// </summary>
    /// <remarks>A <see cref="Color"/> (a plain struct) rather than a <see cref="Brush"/> so the appearance never carries
    /// dispatcher-bound objects across threads.</remarks>
    public Color Background { get; init; } = Colors.Transparent;

    /// <summary>Opacity of the whole overlay in 0..1. Default 1.</summary>
    public double Opacity
    {
        get => _opacity;
        init
        {
            if (double.IsNaN(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value), value, "Opacity must be in 0..1.");
            _opacity = value;
        }
    }

    /// <summary>
    /// Whether the overlay reports its content's desired size for placements that size to content (Right, Left, Above,
    /// Below, Center without an explicit size). Default true. When false, <see cref="Rendering.IOverlayVisual.MeasureAsync"/>
    /// returns null and the placement falls back to the target size. Cover placements always use the target bounds and
    /// stretch the content to fill them.
    /// </summary>
    public bool SizeToContent { get; init; } = true;

    /// <summary>
    /// Optional informational identifier of the factory (for example the id it is registered under in an
    /// <see cref="AppearanceRegistry"/>), used for diagnostics and profile export. It does not affect rendering.
    /// </summary>
    public string? FactoryId { get; init; }

    /// <summary>Creates an appearance from a parameterless factory.</summary>
    /// <param name="factory">Creates the overlay content; invoked on the renderer's dispatcher thread.</param>
    public static WpfAppearance Create(Func<UIElement> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new WpfAppearance(_ => factory());
    }

    /// <summary>Creates an appearance from a factory that receives the <see cref="WpfOverlayContext"/> (rule id, bounds, DPI, dispatcher).</summary>
    /// <param name="factory">Creates the overlay content; invoked on the renderer's dispatcher thread.</param>
    public static WpfAppearance Create(Func<WpfOverlayContext, UIElement> factory) => new(factory);

    /// <summary>Creates an appearance that instantiates <typeparamref name="T"/> with its parameterless constructor.</summary>
    /// <typeparam name="T">Element type, for example a <c>UserControl</c>.</typeparam>
    public static WpfAppearance Create<T>() where T : UIElement, new() =>
        new(static _ => new T()) { FactoryId = typeof(T).FullName };
}
