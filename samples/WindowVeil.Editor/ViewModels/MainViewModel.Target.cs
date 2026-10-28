using WindowVeil.Editor.Model;
using WindowVeil.Platform;

namespace WindowVeil.Editor.ViewModels;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _targetGate = new(1, 1);
    private OverlaySession? _preview;
    private bool _isLivePreview;

    /// <summary>
    /// Live preview: when on, every rule of the list is shown on the target by the preview session
    /// (<see cref="OverlaySession.StartAsync"/> / <see cref="OverlaySession.ResumeAsync"/>); when off, the session is
    /// suspended (<see cref="OverlaySession.SuspendAsync"/>) and overlays are hidden but kept.
    /// </summary>
    public bool IsLivePreview
    {
        get => _isLivePreview;
        set { if (Set(ref _isLivePreview, value)) Fire(ApplyLivePreviewAsync(), "preview"); }
    }

    /// <summary>
    /// Attaches the live-preview session to a window (by handle, <c>WindowSelector.ByHandle</c>) and loads its UI
    /// Automation tree; null detaches. Rules of the list are added to the new session.
    /// </summary>
    public async Task SetTargetAsync(nint? handle)
    {
        await _targetGate.WaitAsync();
        try
        {
            if (_disposed) return;
            await DetachPreviewAsync();
            Target = null;
            TreeRoots.Clear();
            if (handle is not { } h || h == 0)
            {
                SelectWindowSilently(0);
                SessionStateText = "No target";
                return;
            }

            var info = await Task.Run(() => _engine.WindowSystem.GetWindow(h))
                ?? throw new WindowVeilException(WindowVeilErrorCode.InvalidWindowHandle, $"Window 0x{h:X} no longer exists.");
            var session = await Task.Run(() => _engine.AttachAsync(WindowSelector.ByHandle(h)));
            session.StateChanged += OnPreviewStateChanged;
            session.RuleStatusChanged += OnPreviewRuleStatusChanged;
            _preview = session;
            Target = info;
            SessionStateText = session.State.ToString();
            Log.Info("session", $"Preview session attached to {info}.");

            if (!_allWindows.Any(w => w.Handle == h)) await RefreshWindowsAsync();
            SelectWindowSilently(h);
            SuggestProfileApplication(info);

            foreach (var item in Rules) ApplyToSession(item);
            if (_isLivePreview) await ApplyLivePreviewAsync();
            StatusText = $"Target: {info.ProcessName} — \"{info.Title}\"";
            await LoadTreeAsync(h);
        }
        finally
        {
            _targetGate.Release();
        }
    }

    private async Task DetachPreviewAsync()
    {
        var session = _preview;
        _preview = null;
        if (session is null) return;
        session.StateChanged -= OnPreviewStateChanged;
        session.RuleStatusChanged -= OnPreviewRuleStatusChanged;
        await Task.Run(async () => await session.DisposeAsync());
        foreach (var item in Rules) item.SetDetached();
        Log.Info("session", "Preview session detached; its overlays were removed.");
    }

    private async Task ApplyLivePreviewAsync()
    {
        var session = _preview;
        if (session is null)
        {
            if (_isLivePreview) Log.Info("preview", "Live preview starts as soon as a target window is selected.");
            return;
        }
        bool on = _isLivePreview;
        await Task.Run(async () =>
        {
            if (on)
            {
                if (session.State == SessionState.Suspended) await session.ResumeAsync();
                else if (session.State is SessionState.Created or SessionState.Stopped) await session.StartAsync();
            }
            else if (session.State is SessionState.Running or SessionState.WaitingForTarget)
            {
                await session.SuspendAsync();
            }
        });
        Log.Info("preview", on ? $"Live preview on ({Rules.Count} rules)." : "Live preview off (session suspended; overlays hidden).");
    }

    private void OnPreviewStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        // Raised on the session thread (or the caller's thread): marshal to the UI dispatcher.
        _dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _preview)) return;
            SessionStateText = e.NewState.ToString();
            var level = e.NewState is SessionState.TargetLost ? Diagnostics.LogLevel.Warning : Diagnostics.LogLevel.Info;
            Log.Write(level, "session", $"{e.OldState} → {e.NewState}{(e.Reason is null ? "" : ": " + e.Reason)}");
            if (e.NewState == SessionState.TargetLost) StatusText = "The target window closed. Choose another window.";
        });
    }

    private void OnPreviewRuleStatusChanged(object? sender, RuleStatusChangedEventArgs e)
    {
        var status = e.Status;
        _dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _preview)) return;
            var item = Rules.FirstOrDefault(r => r.Id == status.RuleId);
            if (item is null) return;
            string previous = item.State;
            item.Apply(status);
            if (previous == item.State) return;
            switch (status.State)
            {
                case RuleState.Unresolved or RuleState.Failed:
                {
                    string code = status.Error is { } c ? $"[{c}] {ErrorMessages.Explain(c)} " : "";
                    string attempts = status.Resolution?.Attempts is { Count: > 0 } a
                        ? " Attempts: " + string.Join("; ", a.Select(x => $"{x.ResolvedBy?.Kind ?? "?"}={x.Status}"))
                        : "";
                    Log.Warn("rule", $"{status.RuleId}: {status.State}. {code}{status.Message ?? status.Resolution?.Message}{attempts}");
                    break;
                }
                default:
                    Log.Info("rule", status.ToString());
                    break;
            }
        });
    }
}
