# WPF overlays

`WindowVeil.Wpf` displays **any WPF `UIElement`** — built-in controls, your own `UserControl`s and custom XAML
controls — over a region of an external application. Data binding, MVVM, commands, animations, control templates
and resource dictionaries work as in any WPF window, because the content lives in a real WPF window owned by
WindowVeil.

WPF renders through its own DirectX-based pipeline. The WPF backend is therefore **not** graphics-API-free; the
GDI-only native backend (`WindowVeil.Native`) is a separate implementation behind the same renderer contract.

## Setup

```csharp
await using var engine = WindowVeilEngine.CreateBuilder()
    .UseWindowsDesktop()
    .UseWpfOverlays()                       // or .UseWpfOverlays(new WpfOverlayOptions { HostingMode = WpfHostingMode.Grouped })
    .Build();

var session = await engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));
session.AddOverlay(new OverlayRule
{
    Target = ElementLocator.ByAutomationId("StartupCheck"),
    Appearance = WpfAppearance.Create(() => new HiddenSettingOverlay()),
    Placement = OverlayPlacement.CoverTarget,
    InputMode = OverlayInputMode.PassThrough,
});
await session.StartAsync();
```

Declare **Per-Monitor V2** DPI awareness in the hosting application's `app.manifest` (all samples do). WindowVeil's
WPF thread switches itself to PMv2 before creating windows, but WPF renders most accurately when the process is PMv2.

## Factories and threading

```csharp
WpfAppearance.Create(() => new Button { Content = "Custom Action", Width = 140, Height = 36 });
WpfAppearance.Create<MyCustomUserControl>();
WpfAppearance.Create(ctx => new BalanceBadge { DataContext = new BalanceViewModel(ctx) });
```

* The factory runs **on the renderer's dispatcher thread**, once per overlay window, when the overlay is first
  shown (and again if the rule's appearance changes). Create every dispatcher-bound object inside the factory; never
  pass a `UIElement`, `Brush` or `ImageSource` created on another thread (freeze shareable resources if you must).
* By default the renderer owns a dedicated STA thread (`WpfDispatcherHost`). Pass an existing dispatcher with
  `new WpfOverlayOptions { Dispatcher = Application.Current.Dispatcher }` to share your application's UI thread;
  WindowVeil then never shuts that dispatcher down.
* Exceptions thrown by overlay content on WindowVeil's own dispatcher are logged and handled so they cannot crash
  your application. A factory that throws (or returns `null`, a `Window`, or an element that already has a parent)
  makes the rule `Failed` with `OverlayCreationFailed`; no window leaks.

`WpfOverlayContext` (the factory argument) gives view models the rule id, the dispatcher, the target and overlay
bounds in physical pixels and DIPs, and a `TargetBoundsChanged` event raised on the dispatcher whenever the overlay
moves or the target geometry changes.

## Sizing and placement

* `OverlayPlacement.CoverTarget` (and `Cover(padding)`) stretches the content to the target rectangle.
* Adjacent placements (`Right`, `Left`, `Above`, `Below`, `Centered`) use the content's desired size, measured on the
  dispatcher at the target monitor's DPI, unless `Placement.Width`/`Height` are set.
* Window positions are applied in physical pixels directly to the HWND, so overlays stay aligned on mixed-DPI
  setups; the WPF content is sized in DIPs from the window's actual render scale.

## Input and focus

| Mode | Window styles | Behavior |
|---|---|---|
| `OverlayInputMode.PassThrough` (default) | `WS_EX_LAYERED` + `WS_EX_TRANSPARENT` + `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` | purely visual; the mouse reaches the application underneath |
| `OverlayInputMode.Interactive` + `OverlayFocusPolicy.NoActivate` | no `WS_EX_TRANSPARENT`; `WM_MOUSEACTIVATE → MA_NOACTIVATE` | buttons, commands and hover work; the target keeps the foreground and keyboard focus |
| `OverlayInputMode.Interactive` + `OverlayFocusPolicy.ActivateOnClick` | no `WS_EX_NOACTIVATE` | clicking activates the overlay so text boxes can receive keyboard input |

Clicks on an interactive overlay are **not** forwarded to the external application. Fully transparent pixels of an
overlay window are click-through, so only the drawn parts of interactive content capture the mouse.

## Hosting modes

| `WpfHostingMode` | Windows | Trade-off |
|---|---|---|
| `PerOverlay` (default) | one transparent WPF window per overlay | independent z-order per overlay; simplest |
| `Grouped` | one shared window per (target window, input mode, focus policy, z-order) | fewer HWNDs and less composition work for many small components; members share one z-order position and the host spans the union of their bounds |

In grouped mode, pass-through and interactive content never share a window, so a pass-through badge can never
swallow clicks, while gaps between interactive controls still pass clicks through.

## Built-in controls

`WindowVeil.Wpf.Controls.PrivacyBadge` (padlock glyph + text) and `OverlayToolbar` (a row of command buttons) are
ready-made appearances, e.g.

```csharp
Appearance = WpfAppearance.Create(() => new PrivacyBadge { Text = "Hidden by policy" }),
Placement = OverlayPlacement.Right(offsetX: 12, offsetY: 0),
```

## Profiles

WPF object graphs are never serialized. Register a factory id and reference it from JSON:

```csharp
engine.Appearances.Register("hidden-setting", () => WpfAppearance.Create(() => new HiddenSettingOverlay()));
```

```json
"appearance": { "type": "registered", "factory": "hidden-setting" }
```
