<#
.SYNOPSIS
  Downloads the pinned official RetroCap v1.0 x64 release and verifies its SHA-256 before extraction.
.DESCRIPTION
  The digest below was verified against the GitHub release metadata of
  https://github.com/owen800q/RetroCap/releases/tag/v1.0 (asset RetroCap-win-x64.zip).
  The script never downloads an unpinned "latest" release. RetroCap's source is not modified.
.OUTPUTS
  The full path of RetroCap.exe.
#>
[CmdletBinding()]
param(
  [string]$Destination = (Join-Path $PSScriptRoot '..' 'artifacts' 'retrocap'),
  [string]$Tag = 'v1.0',
  [string]$Asset = 'RetroCap-win-x64.zip',
  [string]$ExpectedSha256 = '78e1c59e70f605396658d49c52c5e795f69915a6546887cee0b702062e67d505'
)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$zip = Join-Path $Destination $Asset
$url = "https://github.com/owen800q/RetroCap/releases/download/$Tag/$Asset"

if (-not (Test-Path $zip) -or (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant() -ne $ExpectedSha256) {
  Write-Host "Downloading $url"
  Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
}
$actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
if ($actual -ne $ExpectedSha256) {
  Remove-Item $zip -Force
  throw "SHA-256 mismatch for ${Asset}: expected $ExpectedSha256, got $actual. Refusing to execute an unverified binary."
}
Write-Host "Verified $Asset sha256=$actual"

$extract = Join-Path $Destination 'bin'
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive -Path $zip -DestinationPath $extract
$exe = Get-ChildItem -Path $extract -Filter 'RetroCap.exe' -Recurse | Select-Object -First 1
if (-not $exe) { throw "RetroCap.exe not found in $Asset" }
$exe.FullName
