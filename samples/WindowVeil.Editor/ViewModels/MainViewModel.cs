using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WindowVeil.Automation;
using WindowVeil.Editor.Diagnostics;
using WindowVeil.Editor.Model;
using WindowVeil.Editor.Mvvm;
using WindowVeil.Editor.Services;
using WindowVeil.Platform;
using WindowVeil.Rendering;
using LogLevel = WindowVeil.Editor.Diagnostics.LogLevel;

namespace WindowVeil.Editor.ViewModels;

/// <summary>
/// The editor's main view model: target selection, picker, inspector, hierarchy, rule designer, rule list with live
/// preview, profiles and diagnostics. Every overlay is drawn by a real <see cref="WindowVeilEngine"/>.
/// </summary>
/// <remarks>
/// Threading: the view model lives on the UI thread. UI Automation calls go through <see cref="UiAutomationInspector"/>
/// (its own MTA thread), window enumeration, process listing and file I/O run on the thread pool, and session events
/// (raised on session threads) are marshalled with <see cref="Dispatcher.BeginInvoke(Delegate, object[])"/>.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SimpleLoggerFactory _loggerFactory;
    private readonly WindowVeilEngine _engine;
    private readonly UiAutomationInspector _inspector;
    private readonly ElementHighlighter _highlighter;
    private readonly ElementPicker _picker;
    private readonly EditorAppearanceCatalog _catalog;
    private IEditorDialogs? _dialogs;
    private bool _disposed;

    private List<WindowItem> _allWindows = [];
    private List<ProcessItem> _allProcesses = [];
    private ProcessItem? _selectedProcess;
    private WindowItem? _selectedWindow;
    private bool _suppressWindowSelection;
    private string _processFilter = "";
    private TargetWindowInfo? _target;
    private string _sessionStateText = "No target";
    private string _statusText = "Pick an element with the crosshair, or choose a window.";

    /// <summary>Creates the engine, the inspector and every service.</summary>
    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Log = new DiagnosticLog(dispatcher);
        _loggerFactory = new SimpleLoggerFactory(new DiagnosticLogLoggerProvider(Log, Microsoft.Extensions.Logging.LogLevel.Warning));

        // The logger factory must be set before UseWpfOverlays, which captures it for the WPF renderer.
        _engine = WindowVeilEngine.CreateBuilder()
            .UseLoggerFactory(_loggerFactory)
            .UseWindowsDesktop()
            .UseWpfOverlays()
            .Build();
        _inspector = new UiAutomationInspector(logger: _loggerFactory.CreateLogger("WindowVeil.Editor.Inspector"));
        _highlighter = new ElementHighlighter(_engine, Log);
        _picker = new ElementPicker(_inspector, new DispatcherSynchronizationContext(dispatcher), ex => Log.Warn("picker", ErrorMessages.Describe(ex)));
        _picker.Hovered += OnPickerHovered;
        _catalog = new EditorAppearanceCatalog(_engine.Appearances);

        Designer = new RuleDesignerViewModel(CurrentWindowSnapshot, _catalog, () => _engine.Appearances.Ids);

        RefreshProcessesCommand = new AsyncRelayCommand(RefreshProcessesAsync, onError: ReportError);
        RefreshWindowsCommand = new AsyncRelayCommand(RefreshWindowsAsync, onError: ReportError);
        DetachCommand = new AsyncRelayCommand(() => SetTargetAsync(null), () => _target is not null, ReportError);
        CopyLogCommand = new RelayCommand(CopyLog);
        ClearLogCommand = new RelayCommand(Log.Clear);
        InitializeInspectorCommands();
        InitializeRuleCommands();
        RegisterGallery();

        Log.Info("editor", "WindowVeil engine ready (native + WPF renderers, UI Automation, WinEvent tracking). OCR is not configured in the editor.");
    }

    /// <summary>Diagnostics.</summary>
    public DiagnosticLog Log { get; }

    /// <summary>The engine (exposed for extensions, e.g. registering more appearances).</summary>
    public WindowVeilEngine Engine => _engine;

    /// <summary>The WPF appearance gallery / extension point.</summary>
    public EditorAppearanceCatalog Catalog => _catalog;

    /// <summary>The rule designer.</summary>
    public RuleDesignerViewModel Designer { get; }

    /// <summary>Status bar text.</summary>
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>Connects view services (called once by the main window).</summary>
    public void AttachView(IEditorDialogs dialogs) => _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    /// <summary>Initial data load.</summary>
    public async Task InitializeAsync()
    {
        await RefreshProcessesAsync();
        await RefreshWindowsAsync();
    }

    // ================================================================ processes and windows

    /// <summary>Running processes with a main window.</summary>
    public ObservableCollection<ProcessItem> Processes { get; } = [];

    /// <summary>Visible, titled top-level windows of other processes (filtered by the selected process).</summary>
    public ObservableCollection<WindowItem> Windows { get; } = [];

    /// <summary>Refreshes the process list.</summary>
    public ICommand RefreshProcessesCommand { get; }

    /// <summary>Refreshes the window list.</summary>
    public ICommand RefreshWindowsCommand { get; }

    /// <summary>Detaches from the target.</summary>
    public ICommand DetachCommand { get; }

    /// <summary>Text filter for the process list.</summary>
    public string ProcessFilter
    {
        get => _processFilter;
        set { if (Set(ref _processFilter, value ?? "")) ApplyProcessFilter(); }
    }

    /// <summary>Selected process; filters the window list (null = all windows).</summary>
    public ProcessItem? SelectedProcess
    {
        get => _selectedProcess;
        set { if (Set(ref _selectedProcess, value)) ApplyWindowFilter(); }
    }

    /// <summary>Selected window; selecting attaches the live preview session to it.</summary>
    public WindowItem? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (!Set(ref _selectedWindow, value) || _suppressWindowSelection) return;
            if (value is not null && value.Handle != _target?.Handle) Fire(SetTargetAsync(value.Handle), "attach");
        }
    }

    /// <summary>The target window snapshot (null when detached).</summary>
    public TargetWindowInfo? Target
    {
        get => _target;
        private set
        {
            if (!Set(ref _target, value)) return;
            OnPropertiesChanged(nameof(TargetText), nameof(HasTarget));
        }
    }

    /// <summary>Whether a target is attached.</summary>
    public bool HasTarget => _target is not null;

    /// <summary>Target description.</summary>
    public string TargetText => _target is null ? "No target window" : $"0x{_target.Handle:X} · {_target.ProcessName} · \"{_target.Title}\" · {_target.Bounds}";

    /// <summary>Preview session state.</summary>
    public string SessionStateText { get => _sessionStateText; private set => Set(ref _sessionStateText, value); }

    private async Task RefreshProcessesAsync()
    {
        int ownId = Environment.ProcessId;
        _allProcesses = await Task.Run(() =>
        {
            var result = new List<ProcessItem>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == ownId) continue;
                        string title = p.MainWindowTitle;
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        result.Add(new ProcessItem(p.Id, p.ProcessName, title));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // exited or access denied: skip
                    }
                }
            }
            return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();
        });
        ApplyProcessFilter();
    }

    private void ApplyProcessFilter()
    {
        string filter = _processFilter.Trim();
        var selectedId = _selectedProcess?.Id;
        Processes.Clear();
        foreach (var p in _allProcesses)
        {
            if (filter.Length > 0 && !p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !p.MainWindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Processes.Add(p);
        }
        SelectedProcess = selectedId is { } id ? Processes.FirstOrDefault(p => p.Id == id) : null;
    }

    private async Task RefreshWindowsAsync()
    {
        int ownId = Environment.ProcessId;
        var windows = await Task.Run(() => _engine.WindowSystem.EnumerateWindows()
            .Where(w => w.IsVisible && !string.IsNullOrWhiteSpace(w.Title) && w.ProcessId != ownId && !OverlayWindowRegistry.IsOverlay(w.Handle))
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .Select(w => new WindowItem(w))
            .ToList());
        _allWindows = windows;
        ApplyWindowFilter();
    }

    private void ApplyWindowFilter()
    {
        var keep = _selectedWindow?.Handle;
        _suppressWindowSelection = true;
        try
        {
            Windows.Clear();
            foreach (var w in _allWindows.Where(w => _selectedProcess is null || w.Info.ProcessId == _selectedProcess.Id)) Windows.Add(w);
            SelectedWindow = Windows.FirstOrDefault(w => w.Handle == keep) ?? (_target is null ? null : Windows.FirstOrDefault(w => w.Handle == _target.Handle));
        }
        finally { _suppressWindowSelection = false; }
    }

    /// <summary>Fresh snapshot of the target window (any thread), or null.</summary>
    private TargetWindowInfo? CurrentWindowSnapshot() => _target is null ? null : _engine.WindowSystem.GetWindow(_target.Handle);

    /// <summary>Selects the window in the list without re-triggering attachment.</summary>
    private void SelectWindowSilently(nint handle)
    {
        _suppressWindowSelection = true;
        try { SelectedWindow = Windows.FirstOrDefault(w => w.Handle == handle); }
        finally { _suppressWindowSelection = false; }
    }

    // ================================================================ diagnostics

    /// <summary>Copies the log to the clipboard.</summary>
    public ICommand CopyLogCommand { get; }

    /// <summary>Clears the log.</summary>
    public ICommand ClearLogCommand { get; }

    private void CopyLog()
    {
        if (_dialogs?.CopyToClipboard(Log.ToText()) == false) Log.Warn("editor", "The clipboard is busy; try again.");
    }

    /// <summary>Logs an exception with a friendly message (UI thread or any thread).</summary>
    public void ReportError(Exception ex)
    {
        string text = ErrorMessages.Describe(ex);
        Log.Write(ex is OperationCanceledException ? LogLevel.Info : LogLevel.Error, "editor", text);
        if (_dispatcher.CheckAccess()) StatusText = text;
        else _dispatcher.BeginInvoke(() => StatusText = text);
    }

    /// <summary>Observes a fire-and-forget task so its exceptions reach the diagnostics panel.</summary>
    private async void Fire(Task task, string what)
    {
        try { await task; }
        catch (Exception ex)
        {
            Log.Error(what, ErrorMessages.Describe(ex));
            StatusText = ErrorMessages.Describe(ex);
        }
    }

    // ================================================================ disposal

    /// <summary>Disposes the picker, the highlight and preview sessions, the engine (removing every overlay) and the inspector.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _picker.CancelAsync(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { await _highlighter.DisposeAsync(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { await DetachPreviewAsync(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { await _engine.DisposeAsync(); } catch (Exception ex) { Debug.WriteLine(ex); }
        try { _inspector.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
        _loggerFactory.Dispose();
    }
}
