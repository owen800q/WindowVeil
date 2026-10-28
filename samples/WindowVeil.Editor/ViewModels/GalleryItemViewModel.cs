using System.Windows;
using System.Windows.Input;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.Services;

namespace WindowVeil.Editor.ViewModels;

/// <summary>
/// A gallery tile: either a native appearance preset (solid/transparent mask, highlight) or a WPF appearance from the
/// <see cref="EditorAppearanceCatalog"/>. The preview element is created lazily on the UI thread.
/// </summary>
public sealed class GalleryItemViewModel
{
    private readonly Func<UIElement> _previewFactory;
    private UIElement? _preview;

    private GalleryItemViewModel(string id, string displayName, string description, Func<UIElement> previewFactory, AppearanceType type, CatalogEntry? entry, ICommand useCommand)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        _previewFactory = previewFactory;
        AppearanceType = type;
        Entry = entry;
        UseCommand = useCommand;
    }

    /// <summary>Creates a tile for a native appearance preset.</summary>
    public static GalleryItemViewModel ForPreset(string id, string displayName, string description, AppearanceType type, Func<UIElement> preview, ICommand useCommand) =>
        new(id, displayName, description, preview, type, null, useCommand);

    /// <summary>Creates a tile for a registered WPF appearance.</summary>
    public static GalleryItemViewModel ForCatalog(CatalogEntry entry, ICommand useCommand)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Id, entry.DisplayName, entry.Description, entry.Factory, AppearanceType.GalleryItem, entry, useCommand);
    }

    /// <summary>Id (registered factory id for WPF items).</summary>
    public string Id { get; }

    /// <summary>Display name.</summary>
    public string DisplayName { get; }

    /// <summary>Description.</summary>
    public string Description { get; }

    /// <summary>The designer appearance type this tile selects.</summary>
    public AppearanceType AppearanceType { get; }

    /// <summary>The catalog entry for WPF items (null for native presets).</summary>
    public CatalogEntry? Entry { get; }

    /// <summary>Whether the tile is a registered WPF appearance.</summary>
    public bool IsWpf => Entry is not null;

    /// <summary>Kind label.</summary>
    public string KindText => IsWpf ? $"WPF · registered \"{Id}\"" : "Native renderer";

    /// <summary>Live preview, created once on first access (UI thread). A failing factory shows its error instead.</summary>
    public UIElement Preview
    {
        get
        {
            if (_preview is not null) return _preview;
            try
            {
                _preview = _previewFactory();
            }
            catch (Exception ex)
            {
                _preview = new System.Windows.Controls.TextBlock { Text = "Preview failed: " + ex.Message, TextWrapping = TextWrapping.Wrap };
            }
            return _preview;
        }
    }

    /// <summary>Applies this appearance to the rule designer (parameter: this tile).</summary>
    public ICommand UseCommand { get; }
}
