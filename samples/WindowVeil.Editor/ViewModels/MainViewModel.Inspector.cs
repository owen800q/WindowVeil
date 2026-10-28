using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using WindowVeil.Automation;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.Mvvm;
using WindowVeil.Editor.Services;
using WindowVeil.Geometry;

namespace WindowVeil.Editor.ViewModels;

public sealed partial class MainViewModel
{
    private const int MaxRevealDepth = 64;
    private static readonly TimeSpan SelectionHighlightDuration = TimeSpan.FromSeconds(2.5);

    private readonly Dictionary<int, string> _processNames = [];
    private bool _isPicking;
    private string _hoverText = "Drag the crosshair onto an element of another application.";
    private AutomationElementInfo? _picked;
    private AutomationElementInfo? _inspected;
    private string _inspectedProcess = "";
    private ElementNodeViewModel? _selectedNode;
    private bool _selectingNodeFromCode;

    private void InitializeInspectorCommands()
    {
        RevealPickedCommand = new AsyncRelayCommand(() => _picked is null ? Task.CompletedTask : RevealAsync(_picked), () => _picked is not null, ReportError);
        HighlightInspectedCommand = new RelayCommand(HighlightInspected, () => _inspected is { Bounds.IsEmpty: false });
        RefreshInspectedCommand = new AsyncRelayCommand(RefreshInspectedAsync, () => _inspected is not null, ReportError);
        ReloadTreeCommand = new AsyncRelayCommand(() => _target is null ? Task.CompletedTask : LoadTreeAsync(_target.Handle), () => _target is not null, ReportError);
        UseSuggestionCommand = new RelayCommand(p => UseSuggestion(p as SuggestionItem));
        DrawRectangleCommand = new AsyncRelayCommand(DrawRectangleAsync, () => _target is not null, ReportError);
        TestLocatorCommand = new AsyncRelayCommand(TestLocatorAsync, () => _target is not null, ReportError);
    }

    // ================================================================ picker

    /// <summary>Whether the crosshair is being dragged.</summary>
    public bool IsPicking { get => _isPicking; private set => Set(ref _isPicking, value); }

    /// <summary>Live description of the hovered element.</summary>
    public string HoverText { get => _hoverText; private set => Set(ref _hoverText, value); }

    /// <summary>Starts a picking gesture (mouse captured by the crosshair).</summary>
    public void BeginPick()
    {
        IsPicking = true;
        _picker.Begin();
        HoverText = "Move over an element… release to select it (Esc cancels).";
    }

    /// <summary>Reports the cursor position in PHYSICAL screen pixels while picking.</summary>
    public void PickMove(PixelPoint screenPoint)
    {
        if (_isPicking) _picker.Move(screenPoint);
    }

    /// <summary>Ends the gesture: selects the element under <paramref name="screenPoint"/> and its top-level window.</summary>
    public async Task EndPickAsync(PixelPoint screenPoint)
    {
        if (!_isPicking) return;
        IsPicking = false;
        try
        {
            var info = await _picker.CompleteAsync(screenPoint);
            _highlighter.Hide();
            if (info is null)
            {
                HoverText = "Nothing pickable there (the editor itself and WindowVeil overlays are ignored).";
                return;
            }
            await SelectPickedAsync(info);
        }
        catch (Exception ex)
        {
            _highlighter.Hide();
            ReportError(ex);
        }
    }

    /// <summary>Cancels the gesture.</summary>
    public void CancelPick()
    {
        if (!_isPicking) return;
        IsPicking = false;
        Fire(_picker.CancelAsync(), "picker");
        _highlighter.Hide();
        HoverText = "Picking cancelled.";
    }

    private void OnPickerHovered(object? sender, AutomationElementInfo? info)
    {
        if (!_isPicking) return;
        if (info is null)
        {
            HoverText = "(nothing pickable here)";
            _highlighter.Hide();
            return;
        }
        HoverText = $"{info.Describe()}  ·  {ProcessNameOf(info.ProcessId)}";
        _highlighter.Show(info.TopLevelWindowHandle, info.Bounds, ElementHighlighter.PickerColor);
    }

    private async Task SelectPickedAsync(AutomationElementInfo info)
    {
        _picked = info;
        Inspect(info);
        HoverText = "Picked: " + info.Describe();
        Log.Info("picker", $"Picked {info.Describe()} in window 0x{info.TopLevelWindowHandle:X}.");
        if (info.TopLevelWindowHandle != 0 && info.TopLevelWindowHandle != _target?.Handle)
            await SetTargetAsync(info.TopLevelWindowHandle);
        await RevealAsync(info);
        ShowSelectionHighlight(info);
        CommandManager.InvalidateRequerySuggested();
    }

    // ================================================================ inspector

    /// <summary>The inspected element.</summary>
    public AutomationElementInfo? InspectedElement
    {
        get => _inspected;
        private set
        {
            if (!Set(ref _inspected, value)) return;
            OnPropertiesChanged(nameof(HasInspected), nameof(InspectedHandleText), nameof(InspectedBoundsText), nameof(InspectedStateText));
        }
    }

    /// <summary>Whether an element is inspected.</summary>
    public bool HasInspected => _inspected is not null;

    /// <summary>Element HWND and top-level HWND.</summary>
    public string InspectedHandleText => _inspected is null ? "" :
        (_inspected.NativeWindowHandle == 0 ? "windowless" : $"0x{_inspected.NativeWindowHandle:X}") + $"  ·  top-level 0x{_inspected.TopLevelWindowHandle:X}";

    /// <summary>Process name and id.</summary>
    public string InspectedProcessText { get => _inspectedProcess; private set => Set(ref _inspectedProcess, value); }

    /// <summary>Bounds text (physical pixels).</summary>
    public string InspectedBoundsText => _inspected is null ? "" : _inspected.Bounds.IsEmpty ? "(no geometry)" : $"{_inspected.Bounds} physical px";

    /// <summary>Offscreen / enabled.</summary>
    public string InspectedStateText => _inspected is null ? "" :
        $"{(_inspected.IsOffscreen ? "Offscreen" : "On screen")}  ·  {(_inspected.IsEnabled ? "Enabled" : "Disabled")}  ·  TextPattern: {(_inspected.SupportsTextPattern ? "yes" : "no")}";

    /// <summary>Locator suggestions for the inspected element, most robust first.</summary>
    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];

    /// <summary>Expands the hierarchy down to the picked element.</summary>
    public ICommand RevealPickedCommand { get; private set; } = null!;

    /// <summary>Highlights the inspected element on the target.</summary>
    public ICommand HighlightInspectedCommand { get; private set; } = null!;

    /// <summary>Re-reads the inspected element.</summary>
    public ICommand RefreshInspectedCommand { get; private set; } = null!;

    /// <summary>Reloads the hierarchy.</summary>
    public ICommand ReloadTreeCommand { get; private set; } = null!;

    /// <summary>"Use as target" for a suggestion (parameter: <see cref="SuggestionItem"/>).</summary>
    public ICommand UseSuggestionCommand { get; private set; } = null!;

    /// <summary>Starts the manual rectangle tool.</summary>
    public ICommand DrawRectangleCommand { get; private set; } = null!;

    /// <summary>Resolves the designer's locator once against the target and highlights the result.</summary>
    public ICommand TestLocatorCommand { get; private set; } = null!;

    private void Inspect(AutomationElementInfo info)
    {
        InspectedElement = info;
        Suggestions.Clear();
        int rank = 1;
        foreach (var locator in info.SuggestedLocators) Suggestions.Add(new SuggestionItem(locator, rank++, UseSuggestionCommand));
        InspectedProcessText = $"{ProcessNameOf(info.ProcessId)} ({info.ProcessId})";
    }

    private string ProcessNameOf(int processId)
    {
        if (_processNames.TryGetValue(processId, out var name)) return name;
        name = _allWindows.FirstOrDefault(w => w.Info.ProcessId == processId)?.Info.ProcessName
               ?? _allProcesses.FirstOrDefault(p => p.Id == processId)?.Name ?? "";
        if (name.Length == 0)
        {
            try
            {
                using var p = Process.GetProcessById(processId);
                name = p.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                name = "?";
            }
        }
        if (_processNames.Count > 512) _processNames.Clear();
        _processNames[processId] = name;
        return name;
    }

    private void UseSuggestion(SuggestionItem? item)
    {
        if (item is null) return;
        Designer.UseLocator(item.Locator);
        Log.Info("designer", $"Target set to {LocatorFormatter.Summary(item.Locator)}.");
        StatusText = "Locator applied to the rule designer. Choose an appearance and click \"Add rule\".";
    }

    private void HighlightInspected()
    {
        if (_inspected is { } info) ShowSelectionHighlight(info);
    }

    private void ShowSelectionHighlight(AutomationElementInfo info)
    {
        if (info.Bounds.IsEmpty || info.TopLevelWindowHandle == 0) return;
        _highlighter.Show(info.TopLevelWindowHandle, info.Bounds, ElementHighlighter.SelectionColor, SelectionHighlightDuration);
    }

    private async Task RefreshInspectedAsync()
    {
        if (_inspected is not { } info) return;
        var fresh = await _inspector.RefreshAsync(info);
        if (fresh is null)
        {
            Log.Warn("inspector", $"The element {info.Describe()} no longer exists.");
            return;
        }
        Inspect(fresh);
        ShowSelectionHighlight(fresh);
    }

    // ================================================================ hierarchy

    /// <summary>Roots of the hierarchy viewer (the target window's UIA element).</summary>
    public ObservableCollection<ElementNodeViewModel> TreeRoots { get; } = [];

    /// <summary>Selected node (set by the view from TreeView.SelectedItemChanged).</summary>
    public ElementNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value) || value?.Info is not { } info) return;
            Inspect(info);
            if (!_selectingNodeFromCode) ShowSelectionHighlight(info);
        }
    }

    private ElementNodeViewModel CreateNode(AutomationElementInfo info) =>
        new(info, null, parent => _inspector.GetChildrenAsync(parent), OnNodeError);

    private void OnNodeError(ElementNodeViewModel node, Exception ex) =>
        Log.Warn("inspector", $"Children of {node.Header} could not be read: {ErrorMessages.Describe(ex)}");

    private async Task LoadTreeAsync(nint hwnd)
    {
        TreeRoots.Clear();
        var root = await _inspector.GetRootAsync(hwnd);
        if (root is null)
        {
            Log.Warn("inspector", $"Window 0x{hwnd:X}: {ErrorMessages.Explain(WindowVeilErrorCode.UiAutomationUnavailable)}");
            return;
        }
        var node = CreateNode(root);
        TreeRoots.Add(node);
        node.IsExpanded = true;
    }

    /// <summary>Expands the hierarchy from the window root down to <paramref name="element"/> and selects it.</summary>
    private async Task RevealAsync(AutomationElementInfo element)
    {
        // Path element → ... → top-level window element (GetParentAsync returns null above the window), bounded.
        var path = new List<AutomationElementInfo> { element };
        var current = element;
        for (int i = 0; i < MaxRevealDepth; i++)
        {
            var parent = await _inspector.GetParentAsync(current);
            if (parent is null) break;
            path.Add(parent);
            current = parent;
        }
        path.Reverse();

        var root = TreeRoots.FirstOrDefault();
        int start = root?.Info is null ? -1 : path.FindIndex(p => p.RuntimeId == root.Info.RuntimeId);
        if (start < 0 && element.TopLevelWindowHandle != 0)
        {
            await LoadTreeAsync(element.TopLevelWindowHandle);
            root = TreeRoots.FirstOrDefault();
            start = root?.Info is null ? -1 : path.FindIndex(p => p.RuntimeId == root.Info.RuntimeId);
        }
        if (root is null || start < 0)
        {
            Log.Warn("inspector", "The picked element could not be located in the hierarchy (its window exposes no UI Automation root).");
            return;
        }

        var node = root;
        for (int i = start + 1; i < path.Count; i++)
        {
            await node.EnsureChildrenLoadedAsync();
            node.IsExpanded = true;
            var next = node.Children.FirstOrDefault(c => c.Info?.RuntimeId == path[i].RuntimeId);
            if (next is null)
            {
                Log.Warn("inspector", string.Create(CultureInfo.InvariantCulture,
                    $"Revealed {i - start} of {path.Count - start - 1} levels: {path[i].Describe()} is not listed under {node.Header} (it may have changed)."));
                break;
            }
            node = next;
        }
        _selectingNodeFromCode = true;
        try { node.IsSelected = true; }
        finally { _selectingNodeFromCode = false; }
    }

    // ================================================================ rectangle tool and locator test

    private async Task DrawRectangleAsync()
    {
        if (_dialogs is null) return;
        var window = await Task.Run(CurrentWindowSnapshot);
        if (window is null) throw new WindowVeilException(WindowVeilErrorCode.InvalidWindowHandle, "The target window no longer exists.");
        if (!window.IsPresentable) throw new WindowVeilException(WindowVeilErrorCode.InvalidState, "Restore the target window before drawing a rectangle.");
        var screen = await _dialogs.DrawRectangleAsync(window);
        if (screen is not { } rect || rect.IsEmpty) { StatusText = "Rectangle tool cancelled."; return; }
        // Re-read the window: positions are stored relative to its current client/window origin.
        window = CurrentWindowSnapshot() ?? window;
        var anchor = Designer.Anchor;
        var relative = ScreenGeometry.ScreenToRelative(rect, window.Bounds, window.ClientBounds, anchor);
        Designer.UseRectangle(relative, anchor);
        _highlighter.Show(window.Handle, rect, ElementHighlighter.SelectionColor, SelectionHighlightDuration);
        Log.Info("designer", $"Rectangle {rect} (screen px) → {relative} relative to the {(anchor == Locators.WindowAnchor.Client ? "client area" : "window")}.");
    }

    private async Task TestLocatorAsync()
    {
        var build = Designer.Build();
        if (build.Rule is not { } rule)
        {
            Log.Warn("designer", "Cannot test: " + string.Join(" ", build.Errors));
            return;
        }
        var window = await Task.Run(CurrentWindowSnapshot);
        if (window is null) throw new WindowVeilException(WindowVeilErrorCode.InvalidWindowHandle, "The target window no longer exists.");
        var resolution = await _engine.ResolveAsync(window, rule.Target);
        if (resolution.IsResolved)
        {
            Log.Info("designer", $"Resolved via {resolution.ResolvedBy?.Kind}: {string.Join(", ", resolution.Bounds)} (confidence {resolution.Confidence:0.##}).");
            _highlighter.Show(window.Handle, resolution.UnionBounds, ElementHighlighter.SelectionColor, SelectionHighlightDuration);
        }
        else
        {
            Log.Warn("designer", $"Not resolved: {resolution.Status} — {resolution.Message}");
        }
        (resolution.State as IDisposable)?.Dispose();
    }
}
