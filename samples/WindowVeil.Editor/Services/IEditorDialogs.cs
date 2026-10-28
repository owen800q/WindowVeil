using WindowVeil.Geometry;
using WindowVeil.Platform;

namespace WindowVeil.Editor.Services;

/// <summary>View services the view model needs (file dialogs, clipboard, rectangle tool), implemented by the main window.</summary>
public interface IEditorDialogs
{
    /// <summary>Asks for a profile to open; null when cancelled.</summary>
    string? PickOpenProfile();

    /// <summary>Asks where to save a profile; null when cancelled.</summary>
    string? PickSaveProfile(string suggestedName);

    /// <summary>Copies text to the clipboard. Returns false when the clipboard is busy.</summary>
    bool CopyToClipboard(string text);

    /// <summary>
    /// Shows the rectangle tool over <paramref name="window"/> and returns the dragged rectangle in PHYSICAL screen
    /// pixels, or null when cancelled.
    /// </summary>
    Task<PixelRect?> DrawRectangleAsync(TargetWindowInfo window);
}
