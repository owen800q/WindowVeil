# API guide

This guide covers the public API by task. Every public member also has XML documentation, so IntelliSense shows
the same information.

## Engine and sessions

```csharp
await using var engine = WindowVeilEngine.CreateBuilder()
    .UseWindowsDesktop()          // WindowVeil.Tracking: Win32 window system, native renderer, WinEvents, UI Automation, capture
    .UseWpfOverlays()             // WindowVeil.Wpf: WpfAppearance support
    .UseOcr(PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory("models")))   // optional
    .Build();

var session = await engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
session.AddRule(MaskRule.Create(ElementLocator.ByAutomationId("StartupCheck"), MaskAppearance.Solid("#202020")));
await session.StartAsync();
```

| Type | Purpose |
|---|---|
| `WindowVeilEngine` | Owns renderers, resolvers and sessions. `AttachAsync(selector)`, `AttachAsync(hwnd)`, `FindWindows`, `ResolveAsync` (one-off resolution for inspectors/tests). Disposing it removes every overlay. |
| `WindowVeilBuilder` | Fluent composition: `UseWindowSystem`, `UseEventSource`, `UseCapture`, `AddResolver`, `AddRenderer`, `RegisterAppearance`, `ConfigureSessions`, `UseLoggerFactory`. Platform packages add `UseWindowsDesktop`, `UseNativeOverlays`, `UseWinEventTracking`, `UseUiAutomation`, `UseScreenCapture`, `UseWpfOverlays`, `UseOcr`. |
| `OverlaySession` | The overlays attached to one target window. Thread-safe rule management, lifecycle and status. |
| `SessionOptions` | `AutoReconnect`, `ReconnectPollInterval`, `ElementRefreshInterval`, `TargetCheckInterval`, `ResolveTimeout`, `AttachTimeout`, `AllowDeferredAttach`. |
| `WindowSelector` | Which window to attach to: `ByProcessName`, `ByTitle`, `ByProcessId`, `ByHandle`, `.WithTitle`, `.WithClassName`, `Predicate`. Re-evaluated on reconnection. |

### Session lifecycle

`Created → Running ⇄ Suspended`, `Running → WaitingForTarget → Running` (target closed and reopened),
`Running → TargetLost` (closed, reconnection disabled), `→ Stopped → Running`, `→ Disposed`.

| Method | Effect |
|---|---|
| `StartAsync` | Subscribes to window events and runs the first update pass before returning, so statuses are meaningful afterwards. |
| `SuspendAsync` / `ResumeAsync` | Hide / show every overlay without destroying windows. |
| `StopAsync` | Destroys overlay windows; rules are kept. |
| `RefreshAsync` | Re-resolves every rule and waits for the pass to complete. |
| `WaitForRuleAsync(id, predicate, timeout)` | Waits for a rule status (useful in tests and scripts). |
| `DisposeAsync` | Stops and detaches from the engine. |

Session events (`RuleStatusChanged`, `StateChanged`) are raised on the session's background thread.

### Rules

```csharp
session.AddRule(new OverlayRule
{
    Id = "balance-badge",
    Target = ElementLocator.ByOcrText("Account Balance", TextMatch.Contains),
    Appearance = WpfAppearance.Create(() => new CustomBalanceOverlay()),
    Placement = OverlayPlacement.Right(offsetX: 12, offsetY: 0),
    InputMode = OverlayInputMode.PassThrough,
    FollowTarget = true,
});
```

| Property | Default | Meaning |
|---|---|---|
| `Id` | new GUID | unique within the session |
| `Target` | required | an `ElementLocator` |
| `Appearance` | required | `MaskAppearance`, `HighlightAppearance`, `SpotlightAppearance`, `WpfAppearance`, `RegisteredAppearance` |
| `Placement` | `CoverTarget` | where the overlay goes relative to the target |
| `InputMode` | `PassThrough` | `Interactive` overlays receive the mouse; clicks are never forwarded to the target |
| `FocusPolicy` | `NoActivate` | `ActivateOnClick` lets interactive WPF controls take keyboard focus |
| `ZOrder` | `AboveTarget` | `Topmost` floats above every normal window (use sparingly) |
| `FollowTarget` | `true` | `false` resolves once and keeps the screen position |
| `Enabled`, `Priority`, `Group`, `Visibility` | | enable state, stacking/resolution order, group toggling, `WhenTargetVisible` / `WhenTargetForeground` |

Rule operations: `AddRule`/`AddOverlay`, `AddRules` (all-or-nothing), `UpdateRule(rule with { ... })`,
`RemoveRule`, `ClearRules`, `EnableRule`, `DisableRule`, `SetGroupEnabled`, `GetRule`, `GetRuleStatus`,
`Rules`, `RuleStatuses`. `UpdateRule` only does the necessary work: a placement change moves the overlay, a locator
change re-resolves, an appearance/input/z-order change recreates the overlay window.

Configuration errors throw `WindowVeilException` from `AddRule`/`UpdateRule` with a `WindowVeilErrorCode`:
`InvalidRule` (validation, duplicate id, unknown registered appearance, no resolver for the locator) or
`RendererNotFound`.

### Rule status

`RuleStatus` reports `State` (`Pending`, `Active`, `Hidden`, `Unresolved`, `Disabled`, `Failed`), the
`Resolution` (status, bounds, confidence, `ResolvedBy`, every fallback attempt), the applied `OverlayBounds`, the
`Renderer`, the overlay window handle and an `Error` code (`ElementNotFound`, `UiAutomationUnavailable`,
`OcrConfidenceTooLow`, `OcrUnavailable`, `CaptureUnavailable`, `OverlayCreationFailed`, ...). An unresolved rule never
shows an overlay.

## Locators

| Factory | Resolved by | Notes |
|---|---|---|
| `ElementLocator.Rectangle(x, y, w, h)` | Core | physical virtual-desktop pixels; does not follow the window |
| `ElementLocator.WindowRelative(x, y, w, h, WindowAnchor.Client)` | Core | physical pixels from the client (or window) origin |
| `ElementLocator.Normalized(x, y, w, h)` / `Window()` | Core | fractions of the client/window size |
| `ElementLocator.ByAutomationId(id)` | Automation | returns a `UiAutomationLocator` |
| `ElementLocator.ByName(name, TextMatch.Exact)` / `ByControlType` / `ByClassName` | Automation | compose with `.WithName`, `.WithControlType`, `.WithClassName`, `.Within(ancestor)`, `.At(index)`; bounded by `MaxDepth`/`MaxNodes` |
| `ElementLocator.ByText(text, TextMatch.Contains)` | Automation | TextPattern ranges (one rectangle per line), otherwise text element names |
| `ElementLocator.ByOcrText(text, TextMatch.Contains)` | Ocr | `MinConfidence`, `SearchRegion`, `.Expanded(insets, mustNotOverlap...)`, `MinInterval` |
| `ElementLocator.FirstOf(...)`, `locator.Or(next)` | Core | ordered fallback |
| `ElementLocator.Smart(automationId, name, text, ocrText, windowRelativeFallback)` | Core | the default UIA → TextPattern → OCR → manual chain |

`TextQuery` / `TextMatch` (`Exact`, `Contains`, `StartsWith`, `Regex`) with `IgnoreCase` and whitespace
normalization are shared by every text-based locator.

Custom locators: implement `ILocatorResolver` (return `LocatorResolution.Found(...)`/`Failed(...)`, honor the
cancellation token, never throw for expected failures) and register it with `builder.AddResolver(...)`.

## Appearances and placement

```csharp
MaskAppearance.Solid("#202020");                 // opaque
MaskAppearance.Transparent("#FF0000", 0.4);      // translucent
HighlightAppearance.Create("#FFB300", thickness: 3);
SpotlightAppearance.Create(opacity: 0.6, SpotlightScope.TargetWindow);
WpfAppearance.Create(() => new MyUserControl()); // WindowVeil.Wpf
new RegisteredAppearance("privacy-badge");       // resolved through engine.Appearances
```

`OverlayPlacement.CoverTarget`, `Cover(padding)`, `Right/Left/Above/Below(offsetX, offsetY)`, `Centered(w, h)`;
`Padding`, `Width`, `Height`, `Alignment`, `MultiRect` (`Each` covers each text line separately, `Union` one
rectangle). Offsets, padding and sizes are DIPs scaled by the target monitor's DPI. `PlacementCalculator` exposes the
same math for custom renderers.

## Geometry

`PixelRect`/`PixelPoint`/`PixelSize` (integers, physical pixels), `RectD`/`PointD` (floating point, space defined by
the producer), `Insets`, `DpiScale`, and `CoordinateConverter` for every conversion between screen, window, client,
normalized, DIP and image coordinates. See [ARCHITECTURE.md](ARCHITECTURE.md#coordinate-spaces).

## Extensibility points

| Interface | Implemented by | Register with |
|---|---|---|
| `IWindowSystem` | `Win32WindowSystem` | `UseWindowSystem` |
| `IWindowEventSource` | `WinEventWindowEventSource`, `PollingWindowEventSource` | `UseEventSource` |
| `IScreenCapture` | `GdiScreenCapture` | `UseCapture` |
| `ILocatorResolver` | geometric, `UiAutomationLocatorResolver`, `OcrLocatorResolver` | `AddResolver` |
| `IOverlayRenderer` / `IOverlayVisual` | `NativeOverlayRenderer`, `WpfOverlayRenderer` | `AddRenderer` |
| `IOcrProvider` | `PaddleOcrEngine` | `UseOcr` |

## Ownership and threading

* The engine owns everything registered through the builder and disposes it.
* Sessions own their overlay windows; renderers own their threads (native message thread, WPF dispatcher).
* All public session and engine methods are thread-safe.
* WPF factories run on the renderer's dispatcher thread; never create WPF objects for an overlay on another thread.
* Native handles are exposed for advanced scenarios (`RuleStatus.OverlayHandle`, `TargetWindowInfo.Handle`) but
  common code never needs them. HWNDs are not stable across restarts — select windows with `WindowSelector`.
