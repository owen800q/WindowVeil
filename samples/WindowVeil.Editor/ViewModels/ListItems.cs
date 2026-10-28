using System.Globalization;
using System.Windows.Input;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.Mvvm;
using WindowVeil.Locators;
using WindowVeil.Platform;

namespace WindowVeil.Editor.ViewModels;

/// <summary>A value with a display label for combo boxes (SelectedValuePath="Value", DisplayMemberPath="Label").</summary>
public sealed record Option<T>(T Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>A running process with a main window.</summary>
public sealed record ProcessItem(int Id, string Name, string MainWindowTitle)
{
    /// <summary>Text shown in the list.</summary>
    public string Display => $"{Name} ({Id})";
}

/// <summary>A top-level window of another application.</summary>
public sealed class WindowItem(TargetWindowInfo info)
{
    /// <summary>Snapshot.</summary>
    public TargetWindowInfo Info { get; } = info;

    /// <summary>HWND.</summary>
    public nint Handle => Info.Handle;

    /// <summary>HWND in hex.</summary>
    public string HandleText => "0x" + Info.Handle.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Process name and id.</summary>
    public string ProcessText => $"{Info.ProcessName} ({Info.ProcessId})";

    /// <summary>Title.</summary>
    public string Title => Info.Title;

    /// <summary>Bounds (physical pixels).</summary>
    public string BoundsText => Info.IsMinimized ? "minimized" : Info.Bounds.ToString();

    /// <inheritdoc />
    public override string ToString() => $"{HandleText} {Info.ProcessName}: {Info.Title}";
}

/// <summary>A locator suggested by the inspector, with a "Use as target" command.</summary>
public sealed class SuggestionItem(ElementLocator locator, int rank, ICommand useCommand)
{
    /// <summary>The locator.</summary>
    public ElementLocator Locator { get; } = locator;

    /// <summary>1-based robustness rank (1 = most robust).</summary>
    public int Rank { get; } = rank;

    /// <summary>Category, e.g. "AutomationId".</summary>
    public string Category => LocatorFormatter.Category(Locator);

    /// <summary>Human-readable description.</summary>
    public string Description => LocatorFormatter.Describe(Locator);

    /// <summary>Applies the locator to the rule designer (parameter: this item).</summary>
    public ICommand UseCommand { get; } = useCommand;
}
