using System.Collections.ObjectModel;
using System.Windows;
using WindowVeil.Appearance;
using WindowVeil.Rendering;
using WindowVeil.Wpf;

namespace WindowVeil.Editor.Services;

/// <summary>A WPF appearance offered in the editor gallery.</summary>
/// <param name="Id">Stable factory id stored in profiles (<c>"appearance": { "type": "registered", "factory": id }</c>).</param>
/// <param name="DisplayName">Name shown in the gallery.</param>
/// <param name="Description">One-line description.</param>
/// <param name="Factory">Creates a NEW element on every call. Runs on WindowVeil's WPF overlay thread for overlays and on
/// the editor's UI thread for gallery previews, so it must not capture dispatcher-bound objects.</param>
/// <param name="SuggestedPlacement">Placement applied when the item is chosen from the gallery (null = keep the current one).</param>
/// <param name="SuggestedInputMode">Input mode applied when the item is chosen (interactive content needs <see cref="OverlayInputMode.Interactive"/>).</param>
public sealed record CatalogEntry(
    string Id,
    string DisplayName,
    string Description,
    Func<UIElement> Factory,
    OverlayPlacement? SuggestedPlacement,
    OverlayInputMode SuggestedInputMode);

/// <summary>
/// The editor's WPF appearance gallery and the <b>extension point for developer-provided overlays</b>.
/// </summary>
/// <remarks>
/// <para><see cref="Register"/> adds a gallery entry (with a live preview in the editor) and registers the same factory
/// in the engine's <see cref="AppearanceRegistry"/> under <paramref name="id"/>, wrapped in a
/// <see cref="WpfAppearance"/>. Rules created in the editor reference it as <c>new RegisteredAppearance(id)</c>, so they
/// export to JSON profiles; an application that loads such a profile registers the same id with its own factory:</para>
/// <code>
/// engine.Appearances.Register("sample-card", () =&gt; WpfAppearance.Create(() =&gt; new SampleCardOverlay()));
/// </code>
/// <para>To add your own controls to the editor, call <see cref="Register"/> from <c>MainViewModel.RegisterGallery</c>
/// (or anywhere before the gallery is shown), for example
/// <c>catalog.Register("balance-badge", "Balance badge", () =&gt; new BalanceBadge());</c>.</para>
/// </remarks>
public sealed class EditorAppearanceCatalog
{
    private readonly AppearanceRegistry _registry;

    /// <summary>Creates a catalog that registers into <paramref name="registry"/> (normally <c>engine.Appearances</c>).</summary>
    public EditorAppearanceCatalog(AppearanceRegistry registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Registered entries, in registration order (UI thread).</summary>
    public ObservableCollection<CatalogEntry> Entries { get; } = [];

    /// <summary>Registers a WPF appearance factory under a stable id (see the class remarks).</summary>
    /// <exception cref="InvalidOperationException">The id is already registered in the engine's registry.</exception>
    public CatalogEntry Register(string id, string displayName, Func<UIElement> factory) =>
        Register(id, displayName, factory, description: "", suggestedPlacement: null, suggestedInputMode: OverlayInputMode.PassThrough);

    /// <summary>Registers a WPF appearance factory with gallery metadata.</summary>
    /// <exception cref="InvalidOperationException">The id is already registered in the engine's registry.</exception>
    public CatalogEntry Register(string id, string displayName, Func<UIElement> factory, string description,
        OverlayPlacement? suggestedPlacement, OverlayInputMode suggestedInputMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(factory);
        // One appearance instance per id: WpfAppearance equality compares the factory delegate, so returning the same
        // instance lets UpdateRule keep the overlay window when only the placement changes.
        var appearance = WpfAppearance.Create(factory) with { FactoryId = id };
        _registry.Register(id, () => appearance);
        var entry = new CatalogEntry(id, displayName, description ?? "", factory, suggestedPlacement, suggestedInputMode);
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Whether <paramref name="id"/> belongs to the gallery.</summary>
    public bool Contains(string id) => Entries.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds an entry by id.</summary>
    public CatalogEntry? Find(string id) => Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}
