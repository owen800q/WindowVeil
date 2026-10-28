# WindowVeil

**Visually mask, hide, highlight, replace and extend parts of external Windows applications — from .NET 8, without
touching them.**

WindowVeil draws overlay windows above another application's window and keeps them glued to the controls you select:
a checkbox found through UI Automation, a text range inside a document, a label recognized by on-device PP-OCRv6, or a
rectangle you pick yourself. Overlays can be native masks, highlights and spotlights, or **any WPF `UIElement`** —
your own `UserControl`s, buttons with commands, bound view models, animations. Think of it as CSS-like visual
customization for desktop applications you cannot change.

No DLL injection, no graphics API hooking, no process memory access. The target application is never modified.

<!-- The release pipeline attaches the comparison screenshot generated on the interactive test runner to every GitHub
     Release (asset retrocap-comparison.png). See docs/RETROCAP-DEMO.md. -->
> **RetroCap demo:** every release embeds a before/after screenshot of the real, unmodified
> [RetroCap](https://github.com/owen800q/RetroCap) v1.0 settings window with its "Start automatically when I sign in to
> Windows" checkbox hidden by WindowVeil. The screenshot is produced by the interactive UI test, never drawn by hand —
> see the latest [release](https://github.com/owen800q/WindowVeil/releases/latest) (asset `retrocap-comparison.png`).

## Features

| | |
|---|---|
| **Native overlays** | Solid and translucent masks, highlight borders, spotlights — Win32 layered windows, GDI fills only |
| **WPF overlays** | Any `UIElement`/`UserControl`; data binding, MVVM, commands, animations, templates; pass-through or interactive; optional shared host windows |
| **Locators** | Absolute / window-relative / normalized rectangles, UI Automation (AutomationId, Name, ControlType, ClassName, ancestors, index), TextPattern text ranges, PP-OCRv6 text, fallback chains that report which strategy won |
| **Tracking** | WinEvent hooks: move, resize, minimize/restore, show/hide, foreground and z-order; DPI- and multi-monitor-aware; coalesced updates; automatic reconnection when the target restarts |
| **OCR** | Local PP-OCRv6 (official ONNX models, ONNX Runtime) for English, Simplified/Traditional Chinese, Japanese and more; result caching, rate limiting, stale-result rejection; captures exclude WindowVeil's own overlays |
| **Rules & profiles** | Declarative rules (add/update/enable/disable/suspend/remove, groups, priorities), versioned JSON profiles with validation and migration |
| **Editor** | WPF editor with crosshair picker, UI Automation inspector, hierarchy viewer, live preview and profile import/export |

## Requirements

* Windows 10 (1809+) or Windows 11, x64 (Arm64 builds of the libraries are untested).
* .NET 8 (`net8.0` for Core, OCR and Profiles; `net8.0-windows` for the Windows packages). .NET 8 is an LTS release
  whose official support ends on **November 10, 2026**; WindowVeil 1.x targets .NET 8 only.
* Building: .NET 8 SDK (`global.json` pins the 8.0 feature band; a newer major SDK is never selected).
* Host applications should declare Per-Monitor V2 DPI awareness in `app.manifest` (see the samples).

## Packages

| Package | Contents |
|---|---|
| `WindowVeil.Core` | Engine, sessions, rules, locators, placement, appearances, geometry, contracts (`net8.0`, no Windows dependencies) |
| `WindowVeil.Native` | Win32 window system and native overlay renderer |
| `WindowVeil.Tracking` | WinEvent tracking and `UseWindowsDesktop()` |
| `WindowVeil.Automation` | UI Automation locators and inspector |
| `WindowVeil.Capture` | Desktop and window capture |
| `WindowVeil.Wpf` | WPF overlay renderer and controls |
| `WindowVeil.Ocr` | OCR abstractions, text matching and the OCR locator (`net8.0`) |
| `WindowVeil.Ocr.Paddle` | PP-OCRv6 via ONNX Runtime (`net8.0`) |
| `WindowVeil.Profiles` | JSON profiles (`net8.0`) |

Packages are attached to every [GitHub Release](https://github.com/owen800q/WindowVeil/releases); NuGet.org
publishing is optional. Install from a downloaded release:

```powershell
dotnet nuget add source C:\path\to\downloaded\packages --name windowveil-local
dotnet add package WindowVeil.Tracking
dotnet add package WindowVeil.Wpf
```

## Quick start: mask an external checkbox

```csharp
using WindowVeil;
using WindowVeil.Appearance;
using WindowVeil.Locators;
using WindowVeil.Platform;

await using var engine = WindowVeilEngine.CreateBuilder()
    .UseWindowsDesktop()          // native overlays + WinEvent tracking + UI Automation + capture
    .Build();

var session = await engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));

session.AddRule(MaskRule.Create(
    locator: ElementLocator.Smart(
        automationId: "StartupCheck",
        name: "Start automatically when I sign in to Windows"),
    appearance: MaskAppearance.Solid("#202020"),
    behavior: MaskBehavior.FollowTarget));

await session.StartAsync();
Console.WriteLine(session.GetRuleStatus(session.Rules[0].Id));   // Active via uiAutomation at [x, y, w×h]
Console.ReadLine();                                                // the mask follows the window until you exit
```

If the element cannot be found, the rule is `Unresolved` with an error code — WindowVeil never guesses coordinates.

## WPF UserControl overlay

```csharp
await using var engine = WindowVeilEngine.CreateBuilder()
    .UseWindowsDesktop()
    .UseWpfOverlays()
    .Build();

var session = await engine.AttachAsync(WindowSelector.ByProcessName("RetroCap"));

session.AddOverlay(new OverlayRule
{
    Target = ElementLocator.ByAutomationId("StartupCheck"),
    Appearance = WpfAppearance.Create(() => new HiddenSettingOverlay()),   // runs on WindowVeil's WPF dispatcher
    Placement = OverlayPlacement.CoverTarget,
    InputMode = OverlayInputMode.PassThrough,
    FollowTarget = true,
});

// An interactive button added to the external window; clicks are handled here, not forwarded.
session.AddOverlay(new OverlayRule
{
    Target = ElementLocator.ByAutomationId("StartMinimizedCheck"),
    Appearance = WpfAppearance.Create(() => new Button { Content = "Custom Action", Width = 140, Height = 36, Command = myCommand }),
    Placement = OverlayPlacement.Right(offsetX: 12, offsetY: 0),
    InputMode = OverlayInputMode.Interactive,
});

await session.StartAsync();
```

See [docs/WPF.md](docs/WPF.md) for threading, input/focus policies and hosting modes.

## UI Automation locators

```csharp
ElementLocator.ByAutomationId("StartupCheck");
ElementLocator.ByName("Apply").WithControlType("Button").At(1);           // second "Apply" button
ElementLocator.ByControlType("CheckBox").WithName("Start", TextMatch.StartsWith).Within(ElementLocator.ByAutomationId("SettingsGroup"));
ElementLocator.ByText("secret token");                                    // TextPattern ranges, one rectangle per line
ElementLocator.ByAutomationId("StartupCheck").Or(ElementLocator.ByName("Start automatically when I sign in to Windows"));
```

Searches are bounded (`MaxDepth`, `MaxNodes`), run on a dedicated per-monitor-DPI-aware thread, cache elements between
updates and report `NotFound`, `Offscreen` or `Unavailable` explicitly.

## PP-OCRv6

```powershell
./tools/download-models.ps1          # pinned official PP-OCRv6 ONNX models, SHA-256 verified (Apache-2.0)
```

```csharp
using var ocr = PaddleOcrEngine.Create(PaddleOcrModelOptions.FromDirectory("models"));

await using var engine = WindowVeilEngine.CreateBuilder()
    .UseWindowsDesktop()
    .UseWpfOverlays()
    .UseOcr(ocr)
    .Build();

var session = await engine.AttachAsync(WindowSelector.ByProcessName("MyApp"));
session.AddOverlay(new OverlayRule
{
    Target = ElementLocator.ByOcrText("Account Balance", TextMatch.Contains),
    Appearance = WpfAppearance.Create(() => new CustomBalanceOverlay()),
    Placement = OverlayPlacement.Right(offsetX: 12, offsetY: 0),
    FollowTarget = true,
});
await session.StartAsync();
```

OCR runs locally; nothing is uploaded and recognized text is not logged by default. The C# pipeline reproduces the
official PaddleOCR pipeline's output on the test fixtures (same texts and polygons). See [docs/OCR.md](docs/OCR.md).

## JSON profiles

```json
{
  "schemaVersion": 1,
  "name": "RetroCap Startup Mask",
  "application": { "processName": "RetroCap" },
  "rules": [
    {
      "id": "hide-startup-checkbox",
      "locator": {
        "type": "uiAutomation",
        "automationId": "StartupCheck",
        "fallbackName": "Start automatically when I sign in to Windows"
      },
      "appearance": { "type": "solid", "color": "#202020", "opacity": 1.0 },
      "placement": "cover",
      "followTarget": true,
      "enabled": true
    }
  ]
}
```

```csharp
var result = ProfileSerializer.LoadFile("retrocap.json");
foreach (var d in result.Diagnostics) Console.WriteLine($"{d.Severity} {d.Path}: {d.Message}");
var session = await engine.AttachProfileAsync(result.EnsureSuccess());   // waits for the app and reconnects after restarts
await session.StartAsync();
```

Profiles never instantiate .NET types named in JSON; custom (e.g. WPF) appearances are referenced by ids the
application registers (`engine.Appearances.Register("hidden-setting", ...)`). Schema: [docs/profile.schema.json](docs/profile.schema.json).

## RetroCap demonstration

`samples/WindowVeil.RetroCapDemo` drives the real RetroCap v1.0 executable (downloaded from the pinned release and
SHA-256 verified): it locates `StartupCheck` through UI Automation, hides it with a native mask, replaces it with a
WPF `HiddenSettingOverlay`, finds the same label with PP-OCRv6, follows moves and resizes, and restores the original
UI — asserting each step on real screenshots. Details and isolation notes: [docs/RETROCAP-DEMO.md](docs/RETROCAP-DEMO.md).

## Samples

| Project | |
|---|---|
| `samples/WindowVeil.Demo` | Interactive showcase (masks, highlight, spotlight, text ranges, WPF badge, interactive toolbar, OCR) against a bundled target |
| `samples/WindowVeil.Editor` | Visual rule editor |
| `samples/WindowVeil.RetroCapDemo` | RetroCap demonstrations A–E and screenshot evidence |
| `samples/WindowVeil.DemoTarget` | Deterministic external target used by tests, demos and benchmarks |

## Build and test

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

* `WindowVeil.UnitTests` and `WindowVeil.OcrTests` run anywhere (OCR tests skip without models unless `WINDOWVEIL_REQUIRE_OCR_MODELS=1`).
* `WindowVeil.NativeTests`, `WindowVeil.WpfTests` and `WindowVeil.UiTests` (`Category=Desktop`) need a Windows desktop session.
* `WindowVeil.UiTests` `Category=Interactive` runs the RetroCap scenarios; use `tools/capture-screenshots.ps1` in an isolated account or VM.

## Publish the demo

```powershell
dotnet publish samples/WindowVeil.Demo/WindowVeil.Demo.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o dist
```

Copy a `models` folder (from `tools/download-models.ps1`) next to `WindowVeil.Demo.exe` to enable the OCR scenario.

## Benchmarks

```powershell
dotnet run -c Release --project benchmarks/WindowVeil.Benchmarks -- --filter *
dotnet run -c Release --project benchmarks/WindowVeil.Benchmarks -- --probe artifacts/benchmarks/resource-probe.md
```

BenchmarkDotNet suites cover native overlay creation/reposition and WPF hosting at 1, 10, 25, 50 and 100 overlays,
UI Automation lookups and PP-OCRv6. The probe reports USER/GDI handle counts, memory, disposal leaks and the CPU cost
and follow latency of tracking a moving external window. Results depend on hardware; record your machine and Windows
configuration with any numbers you publish. Measured OCR timings are in [docs/OCR.md](docs/OCR.md).

## Release and CI/CD

Pull requests run the build, unit, OCR and desktop integration tests on `windows-latest`. Every push to `main`
computes a semantic version from Conventional Commits, rebuilds and retests, runs the real RetroCap UI test on an
interactive Windows runner, verifies the screenshots and artifact hashes, then tags and publishes a GitHub Release.
Nothing is published if any mandatory step fails. See [docs/CI-CD.md](docs/CI-CD.md).

## Known limitations

* **Visual only.** A masked control still exists and works; a pass-through mask leaves it clickable. Use
  `OverlayInputMode.Interactive` to block mouse clicks, but keyboard access is unaffected.
* **Not a redaction tool.** Overlays are separate windows. Window-only capture (`PrintWindow`, some recording tools),
  accessibility APIs and the application itself still see the original content. WindowVeil does not guarantee privacy
  against arbitrary screen capture.
* **Capture.** Some windows cannot be captured reliably and protected content cannot be captured; OCR then reports
  `Unavailable` instead of masking.
* **Accessibility trees can be incomplete.** Some frameworks expose few elements or wrong geometry.
* **OCR can be wrong.** Recognized text and OCR geometry are approximate; use confidence thresholds and validated expansion.
* **Not every application is supported.** Native Win32, WinForms, WPF, Electron/Chromium and other UIA-compatible apps
  are the targets; custom-rendered apps need OCR or manual rectangles.
* **Elevation.** A non-elevated process cannot inspect elevated windows (UIPI).

## Documentation

[Architecture](docs/ARCHITECTURE.md) · [API](docs/API.md) · [WPF](docs/WPF.md) · [OCR](docs/OCR.md) ·
[RetroCap demo](docs/RETROCAP-DEMO.md) · [CI/CD](docs/CI-CD.md)

## License

MIT. PP-OCRv6 models are Apache-2.0 (PaddlePaddle) and are downloaded separately; they are not redistributed in this repository.
