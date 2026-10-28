using System.Diagnostics;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowVeil.Appearance;
using WindowVeil.Capture;
using WindowVeil.Geometry;
using WindowVeil.Imaging;
using WindowVeil.Locators;
using WindowVeil.Native;
using WindowVeil.Ocr;
using WindowVeil.Ocr.Paddle;
using WindowVeil.Platform;
using WindowVeil.RetroCapDemo.Evidence;
using WindowVeil.Text;
using WindowVeil.Wpf;

namespace WindowVeil.RetroCapDemo;

/// <summary>Options for <see cref="RetroCapScenarioRunner"/>.</summary>
public sealed record RetroCapScenarioOptions
{
    /// <summary>Path to the unmodified RetroCap.exe (v1.0 release).</summary>
    public required string RetroCapExe { get; init; }

    /// <summary>Where screenshots and the manifest are written.</summary>
    public required string EvidenceDir { get; init; }

    /// <summary>PP-OCRv6 model root (see tools/download-models.ps1). When null, Demo C cannot run.</summary>
    public string? OcrModelsDir { get; init; }

    /// <summary>Fail (instead of skipping) Demo C when the OCR models are missing.</summary>
    public bool RequireOcr { get; init; }

    /// <summary>Commit the binaries were built from.</summary>
    public string CommitSha { get; init; } = "unknown";

    /// <summary>WindowVeil version string.</summary>
    public string Version { get; init; } = "0.0.0-dev";

    /// <summary>SHA-256 of the verified RetroCap release asset.</summary>
    public string RetroCapAssetSha256 { get; init; } = "78e1c59e70f605396658d49c52c5e795f69915a6546887cee0b702062e67d505";

    /// <summary>Timeout for each wait.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Logging.</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>Progress output (console in the CLI, test output in xUnit).</summary>
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// Runs the RetroCap demonstrations A–E against the real RetroCap.exe and records screenshot evidence:
/// A native mask, B WPF replacement, C PP-OCRv6 locator, D window tracking, E recovery.
/// Only screen geometry is used; RetroCap is never modified, injected into, or clicked. The startup setting is never toggled.
/// </summary>
public static class RetroCapScenarioRunner
{
    /// <summary>The checkbox that is hidden.</summary>
    public const string StartupCheckId = "StartupCheck";

    /// <summary>The neighbor that must stay visible.</summary>
    public const string NeighborCheckId = "StartMinimizedCheck";

    /// <summary>
    /// Neighbor used by RetroCap v1.0, which has no <see cref="NeighborCheckId"/> checkbox: "Hide this window while capturing",
    /// directly above <see cref="StartupCheckId"/>.
    /// </summary>
    public const string LegacyNeighborCheckId = "HideCheck";

    /// <summary>The label of the hidden checkbox.</summary>
    public const string StartupText = "Start automatically when I sign in to Windows";

    /// <summary>The label of the neighbor checkbox.</summary>
    public const string NeighborText = "Start hidden in the notification area";

    /// <summary>Native mask color.</summary>
    public static readonly OverlayColor MaskColor = OverlayColor.Parse("#202020");

    private const int ColorTolerance = 12;

    /// <summary>Runs every scenario and writes screenshots plus <c>evidence-manifest.json</c>. Never throws for scenario failures.</summary>
    public static async Task<EvidenceManifest> RunAsync(RetroCapScenarioOptions o, CancellationToken ct = default)
    {
        Directory.CreateDirectory(o.EvidenceDir);
        var manifest = new EvidenceManifest
        {
            CommitSha = o.CommitSha,
            WindowVeilVersion = o.Version,
            RetroCap = new() { ["version"] = "v1.0", ["assetSha256"] = o.RetroCapAssetSha256, ["exe"] = Path.GetFileName(o.RetroCapExe) },
        };
        var results = new List<ScenarioResult>();
        var ctx = new RunContext(o, manifest, results);
        try
        {
            await ctx.RunAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fatal = new ScenarioResult("setup") { Error = ex.ToString() };
            results.Insert(0, fatal);
            o.Log("FATAL: " + ex);
        }
        manifest.Scenarios = results.Select(ScenarioDto.From).ToList();
        manifest.Result = results.Count > 0 && results.All(r => r.Passed || r.Name.EndsWith("(skipped)", StringComparison.Ordinal)) &&
                          RequiredShots.All(s => manifest.Screenshots.ContainsValue(s)) ? "passed" : "failed";
        manifest.TimestampUtc = DateTimeOffset.UtcNow;
        manifest.Save(Path.Combine(o.EvidenceDir, "evidence-manifest.json"));
        o.Log($"Result: {manifest.Result}");
        return manifest;
    }

    private static readonly string[] RequiredShots =
    [
        "retrocap-before.png", "retrocap-native-mask.png", "retrocap-wpf-mask.png", "retrocap-after-move.png",
        "retrocap-after-resize.png", "retrocap-restored.png", "retrocap-comparison.png",
    ];

    private sealed class RunContext(RetroCapScenarioOptions o, EvidenceManifest manifest, List<ScenarioResult> results)
    {
        private WindowVeilEngine _engine = null!;
        private RetroCapHost _host = null!;
        private PixelRect _screen;
        private BgraImage? _before, _nativeShot;
        private PixelRect _t0, _n0, _window0;

        // Read from the live UI: RetroCap v1.0 labels the checkbox "Start with Windows" and has no StartMinimizedCheck,
        // while later builds use the longer label and add the neighbor below it.
        private string _startupText = StartupText, _neighborId = NeighborCheckId, _neighborText = NeighborText;
        private string? _toggle0;
        private readonly HashSet<nint> _overlayHandles = [];

        private static readonly WindowSelector Selector = WindowSelector.ByProcessName("RetroCap").WithTitle(RetroCapHost.MainWindowTitle);

        public async Task RunAllAsync(CancellationToken ct)
        {
            if (Win32.InteractiveDesktopProblem() is { } problem) throw new InvalidOperationException("No interactive desktop: " + problem);

            var builder = WindowVeilEngine.CreateBuilder()
                .UseLoggerFactory(o.LoggerFactory)
                .UseWindowsDesktop()
                .UseWpfOverlays();
            PaddleOcrEngine? ocr = null;
            if (o.OcrModelsDir is { } models && Directory.Exists(models))
            {
                ocr = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory(models));
                builder.UseOcr(ocr);
            }
            await using var engine = builder.Build();
            _engine = engine;
            await using var host = await RetroCapHost.StartAsync(o.RetroCapExe, TimeSpan.FromSeconds(30), ct);
            _host = host;
            manifest.RetroCap["fileVersion"] = host.FileVersion;

            // Deterministic placement on the primary monitor.
            var raw = Win32.GetWindowBounds(host.MainWindow);
            Win32.Move(host.MainWindow, 120, 120);
            Win32.Activate(host.MainWindow);
            _window0 = await WaitForAsync(() => Win32.GetWindowBounds(host.MainWindow), r => r.X == 120 && r.Y == 120, "window placement", ct);
            _ = raw;

            _screen = engine.WindowSystem.VirtualScreenBounds;
            uint dpi = Win32.GetDpi(host.MainWindow);
            manifest.Environment.Screen = new() { ["x"] = _screen.X, ["y"] = _screen.Y, ["width"] = _screen.Width, ["height"] = _screen.Height };
            manifest.Environment.Dpi = (int)dpi;
            manifest.Environment.ScalePercent = (int)Math.Round(dpi * 100 / 96.0);

            await Step("A. Inspect UI Automation tree", InspectAsync, ct);
            await using var session = await engine.AttachAsync(Selector, new SessionOptions { ElementRefreshInterval = TimeSpan.FromMilliseconds(250) }, ct);
            await Step("A. Native mask", r => NativeMaskAsync(session, r, ct), ct);
            await Step("B. WPF replacement", r => WpfMaskAsync(session, r, ct), ct);
            await Step("D. Window tracking (move)", r => TrackMoveAsync(session, r, ct), ct);
            await Step("D. Window tracking (resize)", r => TrackResizeAsync(session, r, ct), ct);
            if (ocr is not null) await Step("C. PP-OCRv6 locator", r => OcrAsync(session, r, ct), ct);
            else if (o.RequireOcr) results.Add(new ScenarioResult("C. PP-OCRv6 locator") { Error = $"OCR models not found at '{o.OcrModelsDir}'." });
            else results.Add(new ScenarioResult("C. PP-OCRv6 locator (skipped)") { Error = "OCR models not configured." });
            await Step("E. Recovery", r => RecoveryAsync(session, r, ct), ct);
            ocr?.Dispose();
        }

        private async Task Step(string name, Func<ScenarioResult, Task> body, CancellationToken ct)
        {
            var r = new ScenarioResult(name);
            results.Add(r);
            o.Log($"--- {name}");
            try { await body(r); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                r.Error = ex.Message;
                o.Log($"    ERROR {ex}");
                SaveDiagnostic(name);
            }
            foreach (var a in r.Assertions) o.Log($"    [{(a.Passed ? "PASS" : "FAIL")}] {a.Name}: {a.Detail}");
        }

        private Task InspectAsync(ScenarioResult r) => InspectCoreAsync(r, CancellationToken.None);

        private async Task InspectCoreAsync(ScenarioResult r, CancellationToken ct)
        {
            var w = Target();
            var t = await _engine.ResolveAsync(w, ElementLocator.ByAutomationId(StartupCheckId), ct);
            var n = await _engine.ResolveAsync(w, ElementLocator.ByAutomationId(NeighborCheckId), ct);
            if (!n.IsResolved)
            {
                _neighborId = LegacyNeighborCheckId;
                n = await _engine.ResolveAsync(w, ElementLocator.ByAutomationId(_neighborId), ct);
            }
            _startupText = await ReadNameAsync(StartupCheckId) ?? StartupText;
            _neighborText = await ReadNameAsync(_neighborId) ?? NeighborText;
            manifest.RetroCap["startupLabel"] = _startupText;
            manifest.RetroCap["neighbor"] = $"{_neighborId}: {_neighborText}";
            r.Check("StartupCheck found by AutomationId", t.IsResolved, $"{t.Status} {t.UnionBounds} label \"{_startupText}\" {t.Message}");
            r.Check($"Neighbor {_neighborId} found by AutomationId", n.IsResolved, $"{n.Status} {n.UnionBounds} label \"{_neighborText}\"");
            if (!t.IsResolved || !n.IsResolved) return;
            _t0 = t.UnionBounds; _n0 = n.UnionBounds;
            r.TargetBounds = RectDto.From(_t0);
            r.ResolvedBy = t.ResolvedBy?.ToString();
            r.Check("Target inside window", w.Bounds.Contains(_t0), $"window {w.Bounds}, target {_t0}");
            r.Check("Target and neighbor do not overlap", !_t0.IntersectsWith(_n0), $"{_t0} vs {_n0}");
            var byName = await _engine.ResolveAsync(w, ElementLocator.ByName(_startupText), ct);
            r.Check("Name fallback resolves the same element", byName.IsResolved && byName.UnionBounds == _t0, $"{byName.UnionBounds}");
            _toggle0 = await ReadToggleAsync(StartupCheckId);
            r.Check("Toggle state readable (not modified)", _toggle0 is not null, $"StartupCheck toggle = {_toggle0}");
            _before = await ScreenshotAsync("retrocap-before.png", "before", ct);
        }

        private async Task NativeMaskAsync(OverlaySession session, ScenarioResult r, CancellationToken ct)
        {
            session.AddRule(MaskRule.Create(ElementLocator.Smart(automationId: StartupCheckId, name: _startupText), MaskAppearance.Solid(MaskColor), id: "startup"));
            await session.StartAsync(ct);
            var s = await session.WaitForRuleAsync("startup", x => x.State == RuleState.Active, o.StepTimeout, ct);
            _overlayHandles.Add(s.OverlayHandle);
            var overlay = NativeDiagnostics.GetWindowRect(s.OverlayHandle);
            r.TargetBounds = RectDto.From(_t0); r.OverlayBounds = RectDto.From(overlay);
            r.ResolvedBy = s.Resolution?.ResolvedBy?.ToString();
            r.Check("Resolved by UI Automation AutomationId", s.Resolution?.ResolvedBy is UiAutomationLocator { AutomationId: StartupCheckId }, r.ResolvedBy ?? "-");
            r.Check("Overlay bounds equal target bounds", Near(overlay, _t0, 1), $"overlay {overlay}, target {_t0}");
            r.Check("Overlay does not steal focus", Win32.Foreground() == _host.MainWindow, $"foreground 0x{Win32.Foreground():X}");
            await WaitForPixelsAsync(_t0, MaskColor, 0.97, ct);
            var img = await ScreenshotAsync("retrocap-native-mask.png", "nativeMask", ct);
            _nativeShot = img;
            r.Screenshot = "retrocap-native-mask.png";
            CheckMasked(r, img, _t0, MaskColor, 0.97);
            CheckNeighbor(r, img, _n0);
            r.Check("Target had visible content before masking", ImageAnalysis.DistinctColors(_before!, _screen, _t0) >= 3 &&
                ImageAnalysis.FractionNear(_before!, _screen, _t0, MaskColor, ColorTolerance) < 0.5,
                $"distinct colors before = {ImageAnalysis.DistinctColors(_before!, _screen, _t0)}");
            r.Check("Checkbox state unchanged", await ReadToggleAsync(StartupCheckId) == _toggle0, "toggle state compared via UI Automation");

            var cmp = Path.Combine(o.EvidenceDir, "retrocap-comparison.png");
            var crop = _window0.Inflate(40).Intersect(_screen);
            ComparisonImage.Create(_before!, img, _screen, crop, _t0, "Before: original RetroCap settings",
                "After: StartupCheck hidden by a WindowVeil native mask", $"WindowVeil masking RetroCap's \"{_startupText}\" checkbox", cmp);
            manifest.Screenshots["comparison"] = "retrocap-comparison.png";
        }

        private async Task WpfMaskAsync(OverlaySession session, ScenarioResult r, CancellationToken ct)
        {
            var rule = session.GetRule("startup")!;
            session.UpdateRule(rule with { Appearance = WpfAppearance.Create(() => new HiddenSettingOverlay()) });
            var s = await session.WaitForRuleAsync("startup", x => x.State == RuleState.Active && x.Renderer != null && !_overlayHandles.Contains(x.OverlayHandle) && x.OverlayHandle != 0, o.StepTimeout, ct);
            _overlayHandles.Add(s.OverlayHandle);
            var overlay = NativeDiagnostics.GetWindowRect(s.OverlayHandle);
            r.TargetBounds = RectDto.From(_t0); r.OverlayBounds = RectDto.From(overlay);
            r.ResolvedBy = s.Resolution?.ResolvedBy?.ToString();
            r.Check("WPF renderer used", s.Renderer?.Contains("Wpf", StringComparison.OrdinalIgnoreCase) == true, s.Renderer ?? "-");
            r.Check("Overlay bounds equal target bounds", Near(overlay, _t0, 1), $"overlay {overlay}, target {_t0}");
            var bg = OverlayColor.FromRgb(HiddenSettingOverlay.BackgroundColor.R, HiddenSettingOverlay.BackgroundColor.G, HiddenSettingOverlay.BackgroundColor.B);
            await WaitForPixelsAsync(_t0, bg, 0.45, ct);
            var img = await ScreenshotAsync("retrocap-wpf-mask.png", "wpfMask", ct);
            r.Screenshot = "retrocap-wpf-mask.png";
            double frac = ImageAnalysis.FractionNear(img, _screen, _t0, bg, ColorTolerance);
            r.Check("HiddenSettingOverlay background covers the target", frac >= 0.45, $"{frac:P1} of target pixels match {bg}");
            r.Check("Overlay content rendered (icon/label present)", ImageAnalysis.DistinctColors(img, _screen, _t0) >= 4, $"distinct colors {ImageAnalysis.DistinctColors(img, _screen, _t0)}");
            r.Check("Original label no longer visible", ImageAnalysis.Similarity(img, _before!, _screen, _t0, ColorTolerance) < 0.5,
                $"similarity to before {ImageAnalysis.Similarity(img, _before!, _screen, _t0, ColorTolerance):P1}");
            CheckNeighbor(r, img, _n0);
            var hit = Win32.TopLevelWindowAt(_t0.Center);
            r.Check("Pass-through: clicks at the overlay reach RetroCap", hit == _host.MainWindow, $"window at center 0x{hit:X}, RetroCap 0x{_host.MainWindow:X}");
            r.Check("Overlay does not steal focus", Win32.Foreground() == _host.MainWindow, $"foreground 0x{Win32.Foreground():X}");
        }

        private async Task TrackMoveAsync(OverlaySession session, ScenarioResult r, CancellationToken ct)
        {
            var rule = session.GetRule("startup")!;
            session.UpdateRule(rule with { Appearance = MaskAppearance.Solid(MaskColor) });
            await session.WaitForRuleAsync("startup", x => x.State == RuleState.Active && !_overlayHandles.Contains(x.OverlayHandle), o.StepTimeout, ct)
                .ContinueWith(t => { if (t.IsCompletedSuccessfully) _overlayHandles.Add(t.Result.OverlayHandle); }, ct);

            Win32.Move(_host.MainWindow, _window0.X + 160, _window0.Y + 90);
            var expected = _t0.Offset(160, 90);
            var s = await WaitOverlayAsync(session, expected, ct);
            var t1 = await ResolveTargetAsync(ct);
            r.TargetBounds = RectDto.From(t1); r.OverlayBounds = RectDto.From(NativeDiagnostics.GetWindowRect(s.OverlayHandle));
            r.Check("Target moved with the window", t1 == expected, $"target {t1}, expected {expected}");
            r.Check("Native mask followed the move", Near(NativeDiagnostics.GetWindowRect(s.OverlayHandle), t1, 1), $"overlay {r.OverlayBounds}");
            await WaitForPixelsAsync(t1, MaskColor, 0.97, ct);
            var img = await ScreenshotAsync("retrocap-after-move.png", "afterMove", ct);
            r.Screenshot = "retrocap-after-move.png";
            CheckMasked(r, img, t1, MaskColor, 0.97);
            var n1 = _n0.Offset(160, 90);
            r.Check("Neighbor visible after move", ImageAnalysis.FractionNear(img, _screen, n1, MaskColor, ColorTolerance) < 0.05, $"no mask pixels over {_neighborId}");

            // The WPF overlay must follow as well.
            session.UpdateRule(session.GetRule("startup")! with { Appearance = WpfAppearance.Create(() => new HiddenSettingOverlay()) });
            await session.WaitForRuleAsync("startup", x => x.State == RuleState.Active && !_overlayHandles.Contains(x.OverlayHandle), o.StepTimeout, ct)
                .ContinueWith(t => { if (t.IsCompletedSuccessfully) _overlayHandles.Add(t.Result.OverlayHandle); }, ct);
            Win32.Move(_host.MainWindow, _window0.X + 200, _window0.Y + 120);
            var expected2 = _t0.Offset(200, 120);
            var s2 = await WaitOverlayAsync(session, expected2, ct);
            r.Check("WPF overlay followed the move", Near(NativeDiagnostics.GetWindowRect(s2.OverlayHandle), expected2, 1),
                $"overlay {NativeDiagnostics.GetWindowRect(s2.OverlayHandle)}, expected {expected2}");
        }

        private async Task TrackResizeAsync(OverlaySession session, ScenarioResult r, CancellationToken ct)
        {
            // WPF overlay is active from the move scenario. Grow the window by 160 px.
            var wb = Win32.GetWindowBounds(_host.MainWindow);
            Win32.SetBounds(_host.MainWindow, wb with { Width = wb.Width + 160 });
            var grown = await WaitForAsync(() => Win32.GetWindowBounds(_host.MainWindow), b => b.Width == wb.Width + 160, "window resize", ct);
            r.Check("RetroCap accepted the resize", grown.Width == wb.Width + 160, $"{wb} -> {grown}");
            var t2 = await WaitForAsyncAsync(() => ResolveTargetAsync(ct), t => t.Width != _t0.Width || t.X != _t0.X + 200, "target relayout", ct, throwOnTimeout: false);
            var s = await WaitOverlayAsync(session, t2, ct);
            var overlay = NativeDiagnostics.GetWindowRect(s.OverlayHandle);
            r.TargetBounds = RectDto.From(t2); r.OverlayBounds = RectDto.From(overlay);
            r.Check("WPF overlay followed the resize", Near(overlay, t2, 1), $"overlay {overlay}, target {t2} (before resize {_t0.Offset(200, 120)})");
            var bg = OverlayColor.FromRgb(HiddenSettingOverlay.BackgroundColor.R, HiddenSettingOverlay.BackgroundColor.G, HiddenSettingOverlay.BackgroundColor.B);
            await WaitForPixelsAsync(t2, bg, 0.45, ct);
            var img = await ScreenshotAsync("retrocap-after-resize.png", "afterResize", ct);
            r.Screenshot = "retrocap-after-resize.png";
            double frac = ImageAnalysis.FractionNear(img, _screen, t2, bg, ColorTolerance);
            r.Check("Overlay covers the resized target", frac >= 0.45, $"{frac:P1}");
            var n2 = await _engine.ResolveAsync(Target(), ElementLocator.ByAutomationId(_neighborId), ct);
            r.Check("Neighbor visible after resize", n2.IsResolved && ImageAnalysis.FractionNear(img, _screen, n2.UnionBounds, bg, ColorTolerance) < 0.05,
                $"neighbor {n2.UnionBounds}");

            // Native mask must follow a resize too.
            session.UpdateRule(session.GetRule("startup")! with { Appearance = MaskAppearance.Solid(MaskColor) });
            await session.WaitForRuleAsync("startup", x => x.State == RuleState.Active && !_overlayHandles.Contains(x.OverlayHandle), o.StepTimeout, ct)
                .ContinueWith(t => { if (t.IsCompletedSuccessfully) _overlayHandles.Add(t.Result.OverlayHandle); }, ct);
            Win32.SetBounds(_host.MainWindow, grown with { Width = grown.Width - 80 });
            await WaitForAsync(() => Win32.GetWindowBounds(_host.MainWindow), b => b.Width == grown.Width - 80, "window shrink", ct);
            var t3 = await WaitForAsyncAsync(() => ResolveTargetAsync(ct), t => t != t2, "target relayout", ct, throwOnTimeout: false);
            var s3 = await WaitOverlayAsync(session, t3, ct);
            r.Check("Native mask followed the resize", Near(NativeDiagnostics.GetWindowRect(s3.OverlayHandle), t3, 1),
                $"overlay {NativeDiagnostics.GetWindowRect(s3.OverlayHandle)}, target {t3}");

            // Back to the original placement for the remaining scenarios.
            Win32.SetBounds(_host.MainWindow, _window0);
            await WaitForAsync(() => Win32.GetWindowBounds(_host.MainWindow), b => b == _window0, "restore placement", ct);
            await WaitOverlayAsync(session, _t0, ct);
        }

        private async Task OcrAsync(OverlaySession session, ScenarioResult r, CancellationToken ct)
        {
            session.DisableRule("startup");
            var locator = ElementLocator.ByOcrText(_startupText, TextMatch.Contains)
                .Expanded(new Insets(22, 3, 3, 3), TextQuery.Containing(_neighborText)) with { MinConfidence = 0.6 };
            session.AddRule(new OverlayRule { Id = "startup-ocr", Target = locator, Appearance = MaskAppearance.Transparent("#D81B60", 0.6) });
            var s = await session.WaitForRuleAsync("startup-ocr", x => x.State is RuleState.Active or RuleState.Unresolved, TimeSpan.FromSeconds(60), ct);
            r.ResolvedBy = s.Resolution?.ResolvedBy?.ToString();
            r.Check("OCR locator resolved", s.State == RuleState.Active, $"{s.State} {s.Message}");
            if (s.State != RuleState.Active) return;
            var ocrRect = s.Resolution!.UnionBounds;
            r.TargetBounds = RectDto.From(_t0); r.OverlayBounds = RectDto.From(ocrRect);
            r.Check("Resolved by OCR (not UI Automation)", s.Resolution.ResolvedBy is OcrTextLocator, r.ResolvedBy ?? "-");
            r.Check("OCR confidence above threshold", s.Resolution.Confidence >= 0.6, $"{s.Resolution.Confidence:0.000}");
            var glyph = new PixelRect(_t0.X + 2, _t0.Y + _t0.Height / 2 - 4, 8, 8);
            r.Check("Expanded OCR region includes the checkbox square", ocrRect.Contains(glyph), $"ocr {ocrRect}, glyph {glyph}");
            r.Check("Expanded OCR region does not cover the neighbor", !ocrRect.IntersectsWith(_n0), $"ocr {ocrRect}, neighbor {_n0}");
            double iou = Iou(ocrRect, _t0);
            r.Check("OCR geometry agrees with accessibility geometry", iou >= 0.5, $"IoU {iou:0.00} (OCR {ocrRect} vs UIA {_t0})");
            var shot = await ScreenshotAsync("retrocap-ocr-mask.png", "ocrMask", ct);
            r.Screenshot = "retrocap-ocr-mask.png";
            r.Check("Translucent OCR mask visible over the label", ImageAnalysis.Similarity(shot, _before!, _screen, _t0, ColorTolerance) < 0.5,
                $"similarity to before {ImageAnalysis.Similarity(shot, _before!, _screen, _t0, ColorTolerance):P1}");
            session.RemoveRule("startup-ocr");
            session.EnableRule("startup");
            await WaitOverlayAsync(session, _t0, ct);
        }

        private async Task RecoveryAsync(OverlaySession session, ScenarioResult r, CancellationToken ct)
        {
            var s = session.GetRuleStatus("startup");
            _overlayHandles.Add(s.OverlayHandle);
            session.ClearRules();
            await session.RefreshAsync(ct);
            await session.DisposeAsync();
            var alive = _overlayHandles.Where(h => h != 0 && NativeDiagnostics.IsWindow(h)).ToArray();
            r.Check("All overlay windows destroyed", alive.Length == 0, $"{_overlayHandles.Count} overlays created, {alive.Length} still alive");
            await WaitForAsync(() => DesktopScreenshot.CaptureRegion(_t0), img => ImageAnalysis.FractionNear(img, _t0, _t0, MaskColor, ColorTolerance) < 0.05, "mask removed", ct);
            await WaitForStableAsync(_window0, ct);
            var img = await ScreenshotAsync("retrocap-restored.png", "restored", ct);
            r.Screenshot = "retrocap-restored.png";
            r.TargetBounds = RectDto.From(_t0);
            double sim = ImageAnalysis.Similarity(img, _before!, _screen, _t0, ColorTolerance);
            r.Check("Original checkbox visible again", sim >= 0.97, $"similarity to before {sim:P1}");
            double simN = ImageAnalysis.Similarity(img, _before!, _screen, _n0, ColorTolerance);
            r.Check("Neighbor unchanged", simN >= 0.97, $"similarity to before {simN:P1}");
            r.Check("RetroCap responsive", Win32.IsResponsive(_host.MainWindow, TimeSpan.FromSeconds(2)) && !_host.Process.HasExited, "WM_NULL round-trip");
            r.Check("Checkbox state unchanged", await ReadToggleAsync(StartupCheckId) == _toggle0, $"toggle {_toggle0}");
            var t = await ResolveTargetAsync(ct);
            r.Check("UI Automation still reports the original control", t == _t0, $"{t}");
        }

        // ---------------------------------------------------------------- helpers

        private TargetWindowInfo Target() =>
            _engine.WindowSystem.GetWindow(_host.MainWindow) ?? throw new InvalidOperationException("RetroCap window disappeared.");

        private async Task<PixelRect> ResolveTargetAsync(CancellationToken ct)
        {
            var res = await _engine.ResolveAsync(Target(), ElementLocator.ByAutomationId(StartupCheckId), ct);
            return res.IsResolved ? res.UnionBounds : PixelRect.Empty;
        }

        private async Task<RuleStatus> WaitOverlayAsync(OverlaySession session, PixelRect expected, CancellationToken ct)
        {
            await session.RefreshAsync(ct);
            return await session.WaitForRuleAsync("startup",
                x => x.State == RuleState.Active && x.OverlayBounds.Count > 0 && Near(PixelRect.UnionAll(x.OverlayBounds), expected, 1) &&
                     Near(NativeDiagnostics.GetWindowRect(x.OverlayHandle), expected, 1),
                o.StepTimeout, ct);
        }

        private async Task<BgraImage> ScreenshotAsync(string file, string role, CancellationToken ct)
        {
            await WaitForStableAsync(_window0.Union(Win32.GetWindowBounds(_host.MainWindow)).Inflate(40).Intersect(_screen), ct);
            var img = DesktopScreenshot.CaptureRegion(_screen);
            PngCodec.Save(img, Path.Combine(o.EvidenceDir, file));
            manifest.Screenshots[role] = file;
            o.Log($"    screenshot {file} ({img.Width}x{img.Height})");
            return img;
        }

        /// <summary>Waits until two consecutive captures of the region are identical (composition settled).</summary>
        private async Task WaitForStableAsync(PixelRect region, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            NativeDiagnostics.DwmFlush();
            var prev = DesktopScreenshot.CaptureRegion(region);
            while (sw.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(30, ct);
                NativeDiagnostics.DwmFlush();
                var cur = DesktopScreenshot.CaptureRegion(region);
                if (ImageAnalysis.Similarity(prev, cur, region, region, 0) >= 0.999) return;
                prev = cur;
            }
        }

        private async Task WaitForPixelsAsync(PixelRect rect, OverlayColor color, double fraction, CancellationToken ct) =>
            await WaitForAsync(() => DesktopScreenshot.CaptureRegion(rect), img => ImageAnalysis.FractionNear(img, rect, rect, color, ColorTolerance) >= fraction,
                $"{color} over {rect}", ct, throwOnTimeout: false);

        private async Task<T> WaitForAsync<T>(Func<T> probe, Func<T, bool> done, string what, CancellationToken ct, bool throwOnTimeout = true)
        {
            var sw = Stopwatch.StartNew();
            T value = probe();
            while (!done(value))
            {
                if (sw.Elapsed > o.StepTimeout)
                {
                    if (throwOnTimeout) throw new TimeoutException($"Timed out waiting for {what}; last value {value}.");
                    return value;
                }
                await Task.Delay(30, ct);
                NativeDiagnostics.DwmFlush();
                value = probe();
            }
            return value;
        }

        private async Task<T> WaitForAsyncAsync<T>(Func<Task<T>> probe, Func<T, bool> done, string what, CancellationToken ct, bool throwOnTimeout = true)
        {
            var sw = Stopwatch.StartNew();
            T value = await probe();
            while (!done(value))
            {
                if (sw.Elapsed > o.StepTimeout)
                {
                    if (throwOnTimeout) throw new TimeoutException($"Timed out waiting for {what}; last value {value}.");
                    return value;
                }
                await Task.Delay(30, ct);
                value = await probe();
            }
            return value;
        }

        private void CheckMasked(ScenarioResult r, BgraImage img, PixelRect target, OverlayColor color, double min)
        {
            double f = ImageAnalysis.FractionNear(img, _screen, target, color, ColorTolerance);
            r.Check("Mask covers checkbox square and label", f >= min, $"{f:P1} of {target} is {color}");
        }

        private void CheckNeighbor(ScenarioResult r, BgraImage img, PixelRect neighbor)
        {
            double sim = ImageAnalysis.Similarity(img, _before!, _screen, neighbor, ColorTolerance);
            r.Check($"Neighbor {_neighborId} unchanged", sim >= 0.97, $"similarity {sim:P1} over {neighbor}");
        }

        private void SaveDiagnostic(string step)
        {
            try
            {
                var name = "diagnostic-" + new string(step.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant() + ".png";
                PngCodec.Save(DesktopScreenshot.CaptureRegion(_screen.IsEmpty ? new PixelRect(0, 0, 1920, 1080) : _screen), Path.Combine(o.EvidenceDir, name));
            }
            catch (Exception ex) { o.Log("    could not save diagnostic screenshot: " + ex.Message); }
        }

        private Task<string?> ReadNameAsync(string automationId) => Task.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(_host.MainWindow);
                var el = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                var name = el?.Current.Name;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch (ElementNotAvailableException) { return null; }
        });

        private Task<string?> ReadToggleAsync(string automationId) => Task.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(_host.MainWindow);
                var el = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
                return el?.TryGetCurrentPattern(TogglePattern.Pattern, out var p) == true ? ((TogglePattern)p).Current.ToggleState.ToString() : null;
            }
            catch (ElementNotAvailableException) { return null; }
        });

        private static bool Near(PixelRect a, PixelRect b, int tol) =>
            Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.Right - b.Right) <= tol && Math.Abs(a.Bottom - b.Bottom) <= tol;

        private static double Iou(PixelRect a, PixelRect b)
        {
            var i = a.Intersect(b).Area;
            var u = a.Area + b.Area - i;
            return u == 0 ? 0 : (double)i / u;
        }
    }
}
