using System.Collections.ObjectModel;
using WindowVeil.Automation;
using WindowVeil.Editor.Mvvm;

namespace WindowVeil.Editor.ViewModels;

/// <summary>
/// A node of the UI Automation hierarchy viewer. Children are loaded lazily through
/// <see cref="UiAutomationInspector.GetChildrenAsync"/> the first time the node is expanded; until then a placeholder
/// child keeps the expander visible.
/// </summary>
public sealed class ElementNodeViewModel : ObservableObject
{
    private readonly Func<AutomationElementInfo, Task<IReadOnlyList<AutomationElementInfo>>>? _loadChildren;
    private readonly Action<ElementNodeViewModel, Exception>? _onError;
    private Task? _loadTask;
    private bool _isExpanded;
    private bool _isSelected;

    private ElementNodeViewModel(string placeholderText)
    {
        Header = placeholderText;
    }

    /// <summary>Creates a node.</summary>
    public ElementNodeViewModel(AutomationElementInfo info, ElementNodeViewModel? parent,
        Func<AutomationElementInfo, Task<IReadOnlyList<AutomationElementInfo>>> loadChildren, Action<ElementNodeViewModel, Exception> onError)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Parent = parent;
        _loadChildren = loadChildren;
        _onError = onError;
        Header = Format(info);
        if (info.HasChildren != false) Children.Add(new ElementNodeViewModel("Loading…"));
    }

    /// <summary>The element (null for the loading placeholder).</summary>
    public AutomationElementInfo? Info { get; }

    /// <summary>Parent node.</summary>
    public ElementNodeViewModel? Parent { get; }

    /// <summary>Display text.</summary>
    public string Header { get; private set; }

    /// <summary>True for the loading placeholder.</summary>
    public bool IsPlaceholder => Info is null;

    /// <summary>Children (a single placeholder until loaded).</summary>
    public ObservableCollection<ElementNodeViewModel> Children { get; } = [];

    /// <summary>Expanded state (bound to the TreeViewItem); expanding loads children.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value) || !value) return;
            _ = EnsureChildrenLoadedAsync();
        }
    }

    /// <summary>Selected state (bound to the TreeViewItem).</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Loads the children once; later calls return the same task. Never throws.</summary>
    public Task EnsureChildrenLoadedAsync()
    {
        if (Info is null || _loadChildren is null) return Task.CompletedTask;
        return _loadTask ??= LoadAsync();
    }

    /// <summary>Forgets loaded children so they are queried again on the next expand.</summary>
    public void Reset()
    {
        _loadTask = null;
        Children.Clear();
        Children.Add(new ElementNodeViewModel("Loading…"));
        if (_isExpanded) _ = EnsureChildrenLoadedAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var children = await _loadChildren!(Info!); // resumes on the UI thread
            Children.Clear();
            foreach (var child in children) Children.Add(new ElementNodeViewModel(child, this, _loadChildren!, _onError!));
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new ElementNodeViewModel("(children unavailable)"));
            _onError?.Invoke(this, ex);
        }
    }

    private static string Format(AutomationElementInfo info)
    {
        var s = info.ControlType.Length == 0 ? "Element" : info.ControlType;
        if (info.Name.Length > 0) s += " \"" + (info.Name.Length > 48 ? info.Name[..47] + "…" : info.Name) + "\"";
        if (info.AutomationId.Length > 0) s += "  #" + info.AutomationId;
        if (info.IsOffscreen) s += "  (offscreen)";
        return s;
    }
}
