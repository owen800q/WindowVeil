<#
.SYNOPSIS
  Verifies the screenshot evidence produced by the interactive RetroCap UI tests before a release.
.DESCRIPTION
  Checks that every required screenshot exists, is a valid PNG of the recorded screen size, that the
  manifest belongs to the expected commit, and that every scenario and assertion passed. A missing,
  stale or failed evidence set fails the release; nothing is substituted.
#>
[CmdletBinding()]
param(
  [string]$EvidenceDir = (Join-Path $PSScriptRoot '..' 'artifacts' 'screenshots'),
  [string]$ExpectedCommit = $env:GITHUB_SHA,
  [int]$MaxAgeHours = 12
)
$ErrorActionPreference = 'Stop'
$required = @(
  'retrocap-before.png', 'retrocap-native-mask.png', 'retrocap-wpf-mask.png',
  'retrocap-after-move.png', 'retrocap-after-resize.png', 'retrocap-restored.png', 'retrocap-comparison.png'
)
$errors = [System.Collections.Generic.List[string]]::new()

function Read-PngSize([string]$path) {
  $bytes = [IO.File]::ReadAllBytes($path)
  $sig = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
  if ($bytes.Length -lt 33) { return $null }
  for ($i = 0; $i -lt 8; $i++) { if ($bytes[$i] -ne $sig[$i]) { return $null } }
  if ([Text.Encoding]::ASCII.GetString($bytes, 12, 4) -ne 'IHDR') { return $null }
  $w = ($bytes[16] -shl 24) -bor ($bytes[17] -shl 16) -bor ($bytes[18] -shl 8) -bor $bytes[19]
  $h = ($bytes[20] -shl 24) -bor ($bytes[21] -shl 16) -bor ($bytes[22] -shl 8) -bor $bytes[23]
  [pscustomobject]@{ Width = $w; Height = $h; Length = $bytes.Length }
}

$manifestPath = Join-Path $EvidenceDir 'evidence-manifest.json'
if (-not (Test-Path $manifestPath)) { throw "Evidence manifest missing: $manifestPath" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if ($ExpectedCommit -and $manifest.commitSha -ne $ExpectedCommit) {
  $errors.Add("Manifest commit $($manifest.commitSha) does not match expected $ExpectedCommit (stale evidence).")
}
$age = [DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($manifest.timestampUtc)
if ($age.TotalHours -gt $MaxAgeHours) { $errors.Add("Evidence is $([int]$age.TotalHours) h old (limit $MaxAgeHours h).") }
if ($manifest.result -ne 'passed') { $errors.Add("Manifest result is '$($manifest.result)'.") }
foreach ($s in $manifest.scenarios) {
  if (-not $s.passed) { $errors.Add("Scenario '$($s.name)' failed.") }
  foreach ($a in $s.assertions) { if (-not $a.passed) { $errors.Add("Assertion '$($s.name)/$($a.name)' failed: $($a.detail)") } }
}

foreach ($name in $required) {
  $path = Join-Path $EvidenceDir $name
  if (-not (Test-Path $path)) { $errors.Add("Missing screenshot $name"); continue }
  $size = Read-PngSize $path
  if (-not $size) { $errors.Add("$name is not a valid PNG"); continue }
  if ($size.Width -le 0 -or $size.Height -le 0 -or $size.Length -lt 4096) { $errors.Add("$name looks empty ($($size.Width)x$($size.Height), $($size.Length) bytes)") }
  if ($name -ne 'retrocap-comparison.png' -and $manifest.environment.screen) {
    if ($size.Width -ne $manifest.environment.screen.width -or $size.Height -ne $manifest.environment.screen.height) {
      $errors.Add("$name is $($size.Width)x$($size.Height) but the recorded screen is $($manifest.environment.screen.width)x$($manifest.environment.screen.height)")
    }
  }
  $listed = @($manifest.screenshots.PSObject.Properties | ForEach-Object { $_.Value }) -contains $name
  if (-not $listed) { $errors.Add("$name is not referenced by the manifest") }
}

if ($errors.Count -gt 0) {
  $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
  throw "UI evidence verification failed with $($errors.Count) error(s)."
}
Write-Host "UI evidence verified: $($required.Count) screenshots, $(@($manifest.scenarios).Count) scenarios passed for commit $($manifest.commitSha)."
