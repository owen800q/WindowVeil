# RetroCap demonstration

[RetroCap](https://github.com/owen800q/RetroCap) is a .NET 8 WPF screen capture and recording application.
WindowVeil uses its **independently compiled, unmodified** release executable as the primary real-world target.
RetroCap's source is never changed, nothing is injected into its process, and the demo never clicks or toggles
its controls.

## Target

RetroCap's settings window (`RetroCap - Screen Capture`) contains:

```xml
<CheckBox x:Name="StartupCheck" Style="{StaticResource Sap.CheckBox}"
          Content="Start automatically when I sign in to Windows" Margin="0,6,0,0"/>
<CheckBox x:Name="StartMinimizedCheck" Style="{StaticResource Sap.CheckBox}"
          Content="Start hidden in the notification area" Margin="17,6,0,0"/>
```

WPF's automation peer exposes `x:Name` as the UI Automation **AutomationId** when no explicit
`AutomationProperties.AutomationId` is set, and a `CheckBox`'s **Name** is its string content. The demo does not
assume this: step A inspects the live tree and asserts that `StartupCheck` resolves by AutomationId *and* by Name
to the same rectangle before anything is masked. The rule uses
`ElementLocator.Smart(automationId: "StartupCheck", name: "Start automatically when I sign in to Windows")`, so
the Name is a real fallback, and the result records which strategy resolved it.

## Binary

`tools/get-retrocap.ps1` downloads `RetroCap-win-x64.zip` from the pinned
[v1.0 release](https://github.com/owen800q/RetroCap/releases/tag/v1.0) and verifies its SHA-256 before extracting
it. The expected digest `78e1c59e70f605396658d49c52c5e795f69915a6546887cee0b702062e67d505` matches the digest
GitHub records for the release asset. CI never downloads an unpinned "latest" release.

## Scenarios

`samples/WindowVeil.RetroCapDemo` implements every scenario in `RetroCapScenarioRunner`; the interactive UI test
(`tests/WindowVeil.UiTests`, `Category=Interactive`) runs it and fails on any failed assertion.

| Step | What happens | Assertions (on real screenshots / live geometry) |
|---|---|---|
| A. Inspect | Launch RetroCap, place its window at (120,120), resolve `StartupCheck` and `StartMinimizedCheck` through UI Automation. Capture `retrocap-before.png`. | both resolve; target inside the window; the two do not overlap; Name fallback resolves to the same rectangle; toggle state readable |
| A. Native mask | Opaque `#202020` native mask over `StartupCheck`. Capture `retrocap-native-mask.png` and build `retrocap-comparison.png`. | resolved by AutomationId; overlay window rect equals the target rect (±1 px); ≥ 97 % of target pixels are the mask color (checkbox square **and** label hidden); the neighbor region is ≥ 97 % identical to the before screenshot; focus stays on RetroCap; toggle state unchanged |
| B. WPF replacement | Same rule, appearance switched to `HiddenSettingOverlay` (lock icon, "Setting Hidden", custom background, border, padding). Capture `retrocap-wpf-mask.png`. | WPF renderer used; overlay rect equals target rect; overlay background dominates the target; icon/label rendered; original label gone; neighbor unchanged; pass-through (the window under the overlay center is RetroCap); focus unchanged |
| D. Tracking — move | Native mask active, RetroCap moved by (+160, +90). Capture `retrocap-after-move.png`. Then the WPF overlay is activated and the window moved again. | target moved by exactly the delta; native mask followed; mask pixels at the new location; neighbor free of mask pixels; WPF overlay followed the second move |
| D. Tracking — resize | WPF overlay active, window widened by 160 px. Capture `retrocap-after-resize.png`. Then native mask active and the window narrowed by 80 px. | RetroCap accepted the resize; WPF overlay matches the re-resolved target; overlay pixels over the target; neighbor clear; native mask follows the second resize |
| C. OCR | Accessibility rule disabled. PP-OCRv6 recognizes "Start automatically when I sign in to Windows" in a `PrintWindow` capture of RetroCap (overlays excluded), and the label box is expanded 22 DIP to the left (not vertically: the neighbor sits 6 DIP away) to include the checkbox square — validated against the OCR box of "Start hidden in the notification area" (expansion is rejected if it would overlap). Capture `retrocap-ocr-mask.png`. | resolved by the OCR locator (not UI Automation); confidence ≥ 0.6; the expanded box contains the checkbox square and does not intersect the neighbor; IoU with the accessibility rectangle ≥ 0.5 (geometry compared, identities kept separate) |
| E. Recovery | Window restored to its original placement, every rule removed, session disposed. Capture `retrocap-restored.png`. | all overlay windows destroyed; target and neighbor regions ≥ 97 % identical to the before screenshot; RetroCap responds to `WM_NULL`; toggle state unchanged; UI Automation reports the original rectangle |

Screenshots capture the **composed desktop** (GDI `BitBlt` with `CAPTUREBLT`), so independently hosted overlay
windows are included. Before every capture the runner waits for composition to settle (`DwmFlush` until two
consecutive captures are identical) instead of sleeping for fixed periods. If a step fails, a
`diagnostic-*.png` of the desktop is saved.

`evidence-manifest.json` records the commit SHA, WindowVeil version, RetroCap version and asset digest, target and
overlay bounds per scenario, the captured screen size and DPI, screenshot file names and every assertion with its
measured value. `tools/verify-ui-evidence.ps1` re-checks the manifest and PNG files before a release.

## Isolation

On every launch RetroCap synchronizes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RetroCap` with its own
"start with Windows" setting (by default it removes the value) and reads `%APPDATA%\RetroCap\settings.json`. This is
RetroCap's existing behavior, not an effect of WindowVeil. `RetroCapHost` backs up both before launching and
restores them when RetroCap is closed. RetroCap is single-instance; the host refuses to run if a RetroCap instance is
already running (unless `WINDOWVEIL_KILL_EXISTING_RETROCAP=1` on an isolated machine). Run the automated scenarios
in a dedicated test account or VM — see [CI-CD.md](CI-CD.md#tier-b--interactive-windows-runner).

## Running it

```powershell
$exe = ./tools/get-retrocap.ps1 | Select-Object -Last 1
./tools/download-models.ps1                     # PP-OCRv6 models for step C
dotnet run --project samples/WindowVeil.RetroCapDemo -c Release -- --retrocap $exe --evidence artifacts/screenshots --models models --require-ocr

# Or attach to a RetroCap you started yourself and toggle native/WPF overlays by pressing Enter:
dotnet run --project samples/WindowVeil.RetroCapDemo -c Release -- --attach
```
