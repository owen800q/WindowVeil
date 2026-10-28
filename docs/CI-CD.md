# CI/CD

WindowVeil's default branch is `main`. (RetroCap, the external demo target, uses `master`; the two are unrelated.)

Two workflows live in `.github/workflows`:

| Workflow | Trigger | Runner | Credentials |
|---|---|---|---|
| `ci.yml` | pull requests targeting `main`, pushes to other branches, manual | GitHub-hosted `windows-latest`, `ubuntu-latest` | read-only `GITHUB_TOKEN` |
| `release.yml` | every push to `main` (including merged pull requests) | `windows-latest` + the interactive self-hosted runner + `ubuntu-latest` | `contents: write` only in the final `publish` job |

## Tier A — pull request CI (`ci.yml`)

1. Checkout, install the .NET 8 SDK selected by `global.json` (`8.0.x`, `rollForward: latestFeature`, so a newer major SDK is never picked).
2. Download the pinned PP-OCRv6 ONNX models (`tools/download-models.ps1`, SHA-256 verified against `models.lock.json`, cached by the lock file hash).
3. `dotnet restore` and `dotnet build -c Release`. Analyzers run as part of the build (`AnalysisLevel 8.0-default`) and `TreatWarningsAsErrors=true`, so analyzer and nullable warnings fail the job.
4. Unit tests (`WindowVeil.UnitTests`), OCR fixture tests (`WindowVeil.OcrTests`, with `WINDOWVEIL_REQUIRE_OCR_MODELS=1` so missing models fail instead of skipping).
5. Desktop integration tests (`Category=Desktop`) in `WindowVeil.NativeTests`, `WindowVeil.WpfTests` and `WindowVeil.UiTests`. They create their own windows or launch the bundled `WindowVeil.DemoTarget`; they do not use RetroCap. GitHub's Windows images run the runner in a logged-on session, which is enough for window creation, UI Automation and GDI capture.
6. Coverage (coverlet → ReportGenerator summary in the job summary).
7. `dotnet pack` + `tools/validate-packages.ps1`: every expected package exists, has the right version, a real assembly, XML docs and the README; test and sample projects must not produce packages.
8. TRX results are uploaded on every run; the whole `artifacts/` folder is uploaded on failure.

A second job self-tests the versioning script (`tools/tests/compute-version.test.sh`).

## Release pipeline (`release.yml`)

```
push to main
  └─ version     (ubuntu)   tools/compute-version.sh → version, tag, previous tag, skip
      └─ build   (windows)  build → unit + OCR + desktop tests → pack + publish demo/editor → SHA256SUMS
          └─ ui-evidence (self-hosted, interactive)  real RetroCap.exe → overlays → screenshots → verify
              └─ publish (ubuntu, contents: write)   verify hashes + evidence commit → notes → tag + GitHub Release
```

* **Only validated commits are released.** Each job depends on the previous one; a failed or missing job stops the pipeline. The interactive UI test is mandatory: if the self-hosted runner is offline, the job waits in the queue and eventually fails, and nothing is published. Stale screenshots are never reused — `tools/verify-ui-evidence.ps1` rejects evidence whose manifest commit is not `GITHUB_SHA` or that is older than 12 hours.
* **Artifacts correspond to the tested commit.** Packages are built once in the `build` job. Its `SHA256SUMS` file hash is passed as a job output; `publish` recomputes it and runs `sha256sum -c` before uploading, so any change between build and publish fails the release.
* **No duplicates, no moved tags.** A commit that already has a `v*` tag is skipped. `publish` refuses to run if the tag or release already exists. Tags are created by `gh release create --target <sha>`, never force-pushed.
* **Serialized.** The `release` concurrency group runs one release at a time. GitHub keeps at most one *pending* run per group, so if several merges land while a release is running, the newest pending run releases all of them together (its notes list every commit since the last tag).
* **Least privilege.** The workflow default is `contents: read`; only `publish` gets `contents: write`. Pull-request workflows never receive write tokens or run on the self-hosted runner.
* **NuGet.org** publishing is optional and runs only when the `NUGET_API_KEY` secret is configured. GitHub Release packaging works without it.

### Versioning

`tools/compute-version.sh` implements Semantic Versioning from Conventional Commits since the previous `vX.Y.Z` tag reachable from the commit:

| Commits since last tag | `>= 1.0.0` | `0.x` (pre-1.0) |
|---|---|---|
| breaking (`type!:` or `BREAKING CHANGE:` footer) | major | minor |
| `feat:` | minor | minor |
| anything else | patch | patch |

* No tags yet → `v0.1.0`.
* A `Release-As: X.Y.Z` footer forces a version (it must be greater than the last release). This is how `v1.0.0` is cut deliberately.

### Release contents

NuGet packages for every implemented library, `WindowVeil-Demo-win-x64.zip` and `WindowVeil-Editor-win-x64.zip` (self-contained .NET 8 x64), `SHA256SUMS`, the RetroCap screenshots plus `SHA256SUMS.screenshots`, the evidence manifest, the test summary and generated release notes. The notes embed the comparison image through its release-asset URL (`https://github.com/<owner>/<repo>/releases/download/<tag>/retrocap-comparison.png`).

## Recommended branch protection for `main`

* Require a pull request before merging, with at least one approving review.
* Require status checks: `Build and test (windows-latest)` and `Release tooling self-test`.
* Require branches to be up to date before merging; block force pushes and deletions.
* Restrict who can push to `main`, because every push produces a release.

## Tier B — interactive Windows runner

The RetroCap UI test needs a real, unlocked desktop session: overlays are composed by DWM, the test moves and resizes a real window, and screenshots are captured from the composed desktop. GitHub-hosted runners are not guaranteed to provide that reliably, so the release uses a dedicated self-hosted runner.

**Machine**

* Windows 11 23H2 or later (Windows 10 22H2 also works), x64, preferably a disposable VM.
* .NET 8 SDK (installed by `actions/setup-dotnet` from `global.json`), PowerShell 7, Git.
* A dedicated local *standard* (non-administrator) user, e.g. `wv-runner`, used for nothing else.
* Display: a single monitor at **1920×1080** and **100% scaling** (deterministic screenshots). Set it in Settings → Display for the runner account. Mixed-DPI behavior is covered by unit tests, not by the evidence run.
* Disable the screen saver, display sleep and lock screen for the runner account; disable notifications/Focus Assist so toasts don't appear in screenshots.

**Desktop session**

* Configure automatic sign-in for `wv-runner` (Sysinternals Autologon stores the password LSA-encrypted) so a desktop session exists after reboots.
* Install the runner (`config.cmd --labels windowveil-interactive`) and start it **as an interactive process in that session** (`run.cmd` from a scheduled task "At log on of wv-runner", or `config.cmd --runasservice` is *not* suitable because services run in session 0 without a desktop).
* Labels: `self-hosted`, `windows`, `x64`, `windowveil-interactive`.
* Do not connect with RDP during runs; disconnecting an RDP session locks the desktop. Use the console / hypervisor view, or `tscon` the session back to the console before leaving.

**Isolation and permissions**

* Register the runner for this repository only, in a runner group restricted to the `Release` workflow on `main`. `ci.yml` never targets it, so pull-request code cannot run there.
* RetroCap writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RetroCap` on every launch (it syncs the "start with Windows" setting) and stores `%APPDATA%\RetroCap\settings.json`. The interactive tests back up both before launching RetroCap and restore them afterwards; using a dedicated account means the developer's own startup configuration can never be touched. The tests never toggle the startup checkbox.
* The RetroCap binary is downloaded from the pinned `v1.0` release and verified against SHA-256 `78e1c59e70f605396658d49c52c5e795f69915a6546887cee0b702062e67d505` before it is extracted or executed (`tools/get-retrocap.ps1`).
* The runner account needs no administrator rights; no Windows security settings are bypassed.

**Cleanup**

* `actions/checkout` with `clean: true` and the tests kill every RetroCap process they started.
* Snapshot the VM after setup and revert periodically (e.g. weekly) to remove drift.

## Local equivalents

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release                       # all suites; desktop suites need a Windows session
./tools/capture-screenshots.ps1              # interactive RetroCap evidence (isolated account/VM only)
./tools/verify-ui-evidence.ps1 -ExpectedCommit (git rev-parse HEAD)
./tools/package-release.ps1 -Version 0.0.0-local
```
