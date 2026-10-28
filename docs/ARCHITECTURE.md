# Architecture

WindowVeil draws **separate overlay windows** above an external application's window. It never injects code,
hooks graphics APIs, patches memory or changes the target's UI tree. Everything it knows about the target comes
from public Windows APIs (window management, WinEvents, UI Automation, screen capture) and from local OCR.

## Packages and dependencies

```text
                        WindowVeil.Core  (net8.0)
          rules · sessions · locators · placement · appearances · geometry · contracts
             ▲            ▲            ▲             ▲              ▲             ▲
             │            │            │             │              │             │
   WindowVeil.Native  WindowVeil.Wpf  WindowVeil.  WindowVeil.   WindowVeil.Ocr  WindowVeil.Profiles
   (net8.0-windows)   (WPF)           Automation   Capture       (net8.0)        (net8.0)
   Win32 windows,     WPF hosts,      (UIA client)  (GDI/         ▲
   native renderer    dispatcher                    PrintWindow)  │
             ▲                                                   WindowVeil.Ocr.Paddle (net8.0)
             │                                                   PP-OCRv6 · ONNX Runtime
   WindowVeil.Tracking  (WinEvent hooks; composes the Windows desktop defaults)
```

* **Core** is platform-independent and has no WPF, Win32, OCR or application-specific code. It defines the
  contracts every other package implements: `IWindowSystem`, `IWindowEventSource`, `IScreenCapture`,
  `ILocatorResolver`, `IOverlayRenderer`/`IOverlayVisual`, plus the session engine.
* **Native** implements `IWindowSystem` with Win32 and renders masks, highlights and spotlights with layered
  popup windows (GDI fills only — no DirectX/OpenGL/Vulkan).
* **Wpf** renders arbitrary WPF `UIElement`s (any control, `UserControl`, bindings, commands, animations) in
  transparent WPF host windows on a dedicated dispatcher thread. WPF itself renders through DirectX; the WPF
  backend is therefore not graphics-API-free, and it is a separate implementation behind the same contracts.
* **Automation** resolves UI Automation selectors and TextPattern text ranges on a dedicated MTA thread and
  provides the inspector used by the editor.
* **Tracking** turns WinEvent notifications into `WindowEvent`s and provides `UseWindowsDesktop()`, which
  wires the Windows defaults together.
* **Capture** captures the composed desktop (BitBlt + `CAPTUREBLT`) or one window's own content
  (`PrintWindow(PW_RENDERFULLCONTENT)`), with explicit failure results.
* **Ocr** defines `IOcrProvider`, text matching and the OCR locator; **Ocr.Paddle** runs the official PP-OCRv6
  ONNX models locally with ONNX Runtime.
* **Profiles** serializes rules to versioned JSON with validation and migration. Custom appearances are referenced
  by registered ids — JSON can never name .NET types.

## Runtime model

```text
 WinEvent hooks ──► IWindowEventSource ──► session wake (flags OR-ed, coalesced)
                                                  │
          timers (element refresh, liveness, reconnect) ┘
                                                  ▼
                              OverlaySession loop (one background task per session)
   ┌────────────────────────────────────────────────────────────────────────────────────┐
   │ 1. refresh the TargetWindowInfo snapshot (bounds, client, DPI, visibility, z-order)  │
   │ 2. target gone? → hide overlays, release element references, WaitingForTarget        │
   │ 3. Phase A (cheap): pure moves translate cached geometry; render immediately         │
   │ 4. Phase B: re-resolve locators that need it (resize, content change, poll)          │
   │ 5. PlacementCalculator → OverlayFrame per rule → renderer.ApplyAsync (batched)       │
   └────────────────────────────────────────────────────────────────────────────────────┘
```

* WinEvent callbacks only set flags and wake the loop. UI Automation and OCR never run inside callbacks.
* Bursts of events (window drags) are coalesced: while a pass runs, new events accumulate into one flag set.
* A window move without a size change does not re-query UI Automation or OCR; the cached rectangles are
  translated by the move delta, which keeps overlays glued to the window while dragging.
* WPF and many modern UI frameworks have no child HWNDs, so element layout changes inside a window raise few
  window events. Element locators are therefore also re-resolved on a bounded interval
  (`SessionOptions.ElementRefreshInterval`, default 500 ms) and when content-change WinEvents arrive.
* Renderers own their threads. The native renderer applies a whole batch with one
  `BeginDeferWindowPos`/`EndDeferWindowPos`. Overlay windows are created once per rule and reused across moves,
  hides and reconnections.

### Z-order and focus

Overlays are positioned **directly above their target window** in z-order (`OverlayZOrder.AboveTarget`) and
re-stacked whenever the target moves or the foreground window changes. They are not topmost by default, so a
window in front of the target also covers its overlays, and masks never float above unrelated applications.
`OverlayZOrder.Topmost` exists for special cases. Overlay windows use `WS_EX_NOACTIVATE`/`WS_EX_TOOLWINDOW`,
so they never take activation or appear in the taskbar. Pass-through overlays add `WS_EX_TRANSPARENT`
(layered + transparent = mouse input goes to the window below). Interactive overlays receive input and do
**not** forward clicks to the target. No cross-process owner/parent relationships are created (those attach
input queues between processes).

### Locators

A locator is immutable data. `LocatorPipeline` dispatches it to the first registered `ILocatorResolver` that
supports it. `FallbackLocator` (or `ElementLocator.Smart(...)`) chains strategies — by default UI Automation →
TextPattern → PP-OCRv6 → a manual window-relative rectangle — and the result records which strategy resolved
(`LocatorResolution.ResolvedBy`) and every attempt. If every strategy fails the rule is `Unresolved` with an
error code; no coordinates are invented and no overlay is shown.

### Reconnection

Sessions attach through a `WindowSelector` (process name, title, class), not an HWND. When the target closes,
overlays are hidden, cached UI Automation elements are released and the session enters `WaitingForTarget`.
When a matching window appears again (new HWND, new process id), the session subscribes to it, re-resolves
every rule and shows the same overlay windows again.

## Coordinate spaces

All public geometry is explicit about its space (see `CoordinateConverter`):

| Space | Unit | Origin | Used by |
|---|---|---|---|
| Screen | physical pixels | primary monitor top-left, can be negative | `PixelRect` everywhere by default, renderer frames |
| Window / Client | physical pixels | target window / client top-left | `WindowRelativeLocator` |
| Normalized | 0..1 | window / client | `NormalizedLocator` |
| DIP | 1/96 inch | same as screen | placement offsets, padding, sizes; WPF layout |
| Image | pixels of a captured bitmap | capture origin | OCR results (`OcrTextRegion`) |

WindowVeil threads that touch window geometry (native message thread, UI Automation thread, WPF dispatcher)
run Per-Monitor-V2 DPI aware so Windows reports physical coordinates. Placement offsets and sizes are in DIPs
and converted with the target monitor's DPI, so a rule looks the same at 100 %, 125 %, 150 % and 200 %.

## Error model

Expected failures are values, not exceptions: `LocatorResolution.Status`, `RuleStatus.State`/`Error`,
`CaptureResult.Error`, `OcrResult.Status`. Configuration errors (unknown appearance id, no renderer, no resolver
for a locator, duplicate rule ids) throw `WindowVeilException` with a `WindowVeilErrorCode` at `AddRule`, so they
surface immediately. Overlay windows are destroyed on every failure path, on `StopAsync`, on session disposal
and on engine disposal.

## Limitations by design

* Overlays are visual. A masked control still exists and works; a pass-through mask leaves it clickable.
* Masks are separate windows. Window-only capture (for example `PrintWindow`, some recording tools) and
  accessibility APIs still see the original content. WindowVeil does not redact data.
* UI Automation trees can be incomplete; OCR can be wrong, and OCR geometry is approximate.
