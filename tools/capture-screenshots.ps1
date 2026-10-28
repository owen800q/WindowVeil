<#
.SYNOPSIS
  Runs the interactive RetroCap UI tests, which drive the real RetroCap.exe, apply WindowVeil overlays,
  capture composed-desktop screenshots and write an evidence manifest.
.DESCRIPTION
  Requires an interactive Windows desktop session (Tier B runner). Screenshots are produced only by
  the tests themselves; this script never copies or fabricates images. RetroCap updates its own
  HKCU\...\Run entry on launch, so the tests back up and restore that value and RetroCap's
  settings.json — still, run this only in an isolated test account or VM.
#>
[CmdletBinding()]
param(
  [string]$Configuration = 'Release',
  [string]$EvidenceDir = (Join-Path $PSScriptRoot '..' 'artifacts' 'screenshots'),
  [string]$RetroCapExe,
  [string]$Version = '0.0.0-dev'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$EvidenceDir = [IO.Path]::GetFullPath($EvidenceDir)
if (-not $RetroCapExe) { $RetroCapExe = (& (Join-Path $PSScriptRoot 'get-retrocap.ps1') | Select-Object -Last 1) }
if (-not (Test-Path $RetroCapExe)) { throw "RetroCap.exe not found: $RetroCapExe" }

if (Test-Path $EvidenceDir) { Remove-Item $EvidenceDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null

# PP-OCRv6 models for Demo C (required in the release run).
& (Join-Path $PSScriptRoot 'download-models.ps1')
$env:WINDOWVEIL_OCR_MODELS = Join-Path $root 'models'

$env:WINDOWVEIL_RETROCAP_EXE = $RetroCapExe
$env:WINDOWVEIL_EVIDENCE_DIR = $EvidenceDir
$env:WINDOWVEIL_REQUIRE_INTERACTIVE = '1'
$env:WINDOWVEIL_VERSION = $Version
if (-not $env:GITHUB_SHA) { $env:GITHUB_SHA = (git -C $root rev-parse HEAD) }

dotnet test (Join-Path $root 'tests/WindowVeil.UiTests/WindowVeil.UiTests.csproj') `
  -c $Configuration --filter 'Category=Interactive' `
  --logger 'trx;LogFileName=interactive.trx' --logger 'console;verbosity=normal' `
  --results-directory (Join-Path $root 'artifacts' 'test-results')
if ($LASTEXITCODE -ne 0) { throw "Interactive RetroCap UI tests failed (exit $LASTEXITCODE). Diagnostics are in $EvidenceDir." }
Write-Host "Evidence written to $EvidenceDir"
