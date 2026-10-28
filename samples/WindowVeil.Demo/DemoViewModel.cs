using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WindowVeil.Ocr.Paddle;
using WindowVeil.Platform;
using WindowVeil.Profiles;

namespace WindowVeil.Demo;

/// <summary>View model of the demo window. Uses only public WindowVeil APIs.</summary>
public sealed class DemoViewModel : ObservableObject, IAsyncDisposable
{
    private readonly WindowVeilEngine _engine;
    private readonly PaddleOcrEngine? _ocr;
    private readonly System.Text.StringBuilder _log = new();
    private OverlaySession? _session;
    private string _targetTitle = "WindowVeil Demo Target";
    private string _sessionText = "Not attached.";

    /// <summary>Creates the view model and the engine.</summary>
    public DemoViewModel()
    {
        var builder = WindowVeilEngine.CreateBuilder().UseWindowsDesktop().UseWpfOverlays();
        var models = Environment.GetEnvironmentVariable("WINDOWVEIL_OCR_MODELS") ?? Path.Combine(AppContext.BaseDirectory, "models");
        if (Directory.Exists(models))
        {
            try
            {
                _ocr = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(models));
                builder.UseOcr(_ocr);
            }
            catch (Exception ex) { Log($"OCR disabled: {ex.Message}"); }
        }
        _engine = builder.Build();

        LaunchTargetCommand = new RelayCommand(_ => LaunchTarget());
        AttachCommand = new RelayCommand(async _ => await AttachAsync(), _ => _session is null);
        DetachCommand = new RelayCommand(async _ => await DetachAsync(), _ => _session is not null);
        ExportCommand = new RelayCommand(_ => Export(), _ => _session is not null);

        var toolbar = new (string, ICommand)[]
        {
            ("Hide all masks", new RelayCommand(_ => SetAll(true))),
            ("Show all", new RelayCommand(_ => SetAll(false))),
            ("Say hi", new RelayCommand(_ => Log("Toolbar button clicked inside the target window (handled by the demo process)."))),
        };
        Scenarios = new ObservableCollection<DemoScenario>(DemoScenarios.Create(toolbar));
        foreach (var s in Scenarios) s.EnabledChanged += OnScenarioToggled;
        Log(_ocr is null ? "OCR models not found: the OCR scenario is unavailable (run tools/download-models.ps1 and copy models/ next to the exe)." : "PP-OCRv6 loaded.");
    }

    /// <summary>Scenarios.</summary>
    public ObservableCollection<DemoScenario> Scenarios { get; }

    /// <summary>Title text used to select the target window.</summary>
    public string TargetTitle { get => _targetTitle; set => Set(ref _targetTitle, value); }

    /// <summary>Session state text.</summary>
    public string SessionText { get => _sessionText; private set => Set(ref _sessionText, value); }

    /// <summary>Log output.</summary>
    public string LogText => _log.ToString();

    /// <summary>Starts the bundled demo target in a separate process.</summary>
    public ICommand LaunchTargetCommand { get; }

    /// <summary>Attaches to the target.</summary>
    public ICommand AttachCommand { get; }

    /// <summary>Detaches and removes every overlay.</summary>
    public ICommand DetachCommand { get; }

    /// <summary>Exports the serializable rules as a JSON profile.</summary>
    public ICommand ExportCommand { get; }

    private void LaunchTarget()
    {
        var exe = Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo(exe, "--demo-target") { UseShellExecute = false });
        Log("Started the demo target. Click Attach when it appears.");
    }

    private async Task AttachAsync()
    {
        try
        {
            var selector = WindowSelector.ByTitle(TargetTitle);
            _session = await _engine.AttachAsync(selector, new SessionOptions { AttachTimeout = TimeSpan.FromSeconds(10) });
            _session.StateChanged += (_, e) => Ui(() => { SessionText = $"Session: {e.NewState} ({e.Reason})"; Log($"Session {e.OldState} -> {e.NewState}: {e.Reason}"); });
            _session.RuleStatusChanged += (_, e) => Ui(() =>
            {
                var s = Scenarios.FirstOrDefault(x => x.Id == e.Status.RuleId);
                if (s is not null) s.Status = Describe(e.Status);
            });
            foreach (var s in Scenarios.Where(s => s.IsEnabled)) AddRule(s);
            await _session.StartAsync();
            SessionText = $"Attached to {_session.Target}";
            Log(SessionText);
        }
        catch (WindowVeilException ex)
        {
            Log($"Attach failed ({ex.Code}): {ex.Message}");
            _session = null;
        }
    }

    private async Task DetachAsync()
    {
        if (_session is null) return;
        await _session.DisposeAsync();
        _session = null;
        SessionText = "Not attached.";
        foreach (var s in Scenarios) s.Status = "off";
        Log("Detached; every overlay window was destroyed.");
    }

    private void OnScenarioToggled(object? sender, EventArgs e)
    {
        var s = (DemoScenario)sender!;
        if (_session is null) return;
        if (s.IsEnabled) AddRule(s);
        else if (_session.RemoveRule(s.Id)) s.Status = "off";
    }

    private void AddRule(DemoScenario s)
    {
        if (s.RequiresOcr && _ocr is null) { s.Status = "OCR models not available."; s.IsEnabled = false; return; }
        try { _session!.AddRule(s.CreateRule()); s.Status = "pending..."; }
        catch (WindowVeilException ex) { s.Status = $"{ex.Code}: {ex.Message}"; Log(s.Status); }
    }

    private void SetAll(bool hidden)
    {
        if (_session is null) return;
        foreach (var r in _session.Rules.Where(r => r.Id != "toolbar"))
        {
            if (hidden) _session.DisableRule(r.Id); else _session.EnableRule(r.Id);
        }
        Log(hidden ? "Toolbar: masks disabled." : "Toolbar: masks enabled.");
    }

    private void Export()
    {
        if (_session is null) return;
        var serializable = _session.Rules.Where(r => r.Appearance is not Wpf.WpfAppearance).ToArray();
        var profile = ProfileExporter.FromRules("Demo target overlays", _session.Selector, serializable);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "windowveil-demo-profile.json");
        File.WriteAllText(path, ProfileSerializer.ToJson(profile));
        Log($"Exported {serializable.Length} rules to {path} (WPF rules need a registered appearance id and were skipped).");
    }

    private static string Describe(RuleStatus s)
    {
        var by = s.Resolution?.ResolvedBy is { } l ? $" via {l.Kind}" : "";
        var bounds = s.OverlayBounds.Count > 0 ? $" at {Geometry.PixelRect.UnionAll(s.OverlayBounds)}" : "";
        var count = s.OverlayBounds.Count > 1 ? $" ({s.OverlayBounds.Count} rectangles)" : "";
        return $"{s.State}{by}{bounds}{count}{(s.Message is null ? "" : " — " + s.Message)}";
    }

    private void Log(string line)
    {
        _log.AppendLine($"{DateTime.Now:HH:mm:ss} {line}");
        OnPropertyChanged(nameof(LogText));
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        _ocr?.Dispose();
    }
}
