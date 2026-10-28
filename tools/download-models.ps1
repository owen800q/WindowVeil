<#
.SYNOPSIS
    Downloads the pinned PP-OCRv6 ONNX models listed in tools/models.lock.json and verifies their SHA-256.

.DESCRIPTION
    Files land in <Destination>/<model>/<file>, e.g. models/PP-OCRv6_small_det/inference.onnx, which is the layout
    PaddleOcrModelOptions.FromDirectory expects. Every URL names an exact Hugging Face commit; "latest"/main is never
    used. Any size or hash mismatch deletes the partial file and stops with an error (exit code 1).

.PARAMETER Variant
    tiny, small (default), medium or all.

.PARAMETER Destination
    Destination root. Default: <repo>/models.

.PARAMETER Force
    Re-download files even when a verified copy exists.

.EXAMPLE
    pwsh tools/download-models.ps1 -Variant small
#>
[CmdletBinding()]
param(
    [ValidateSet('tiny', 'small', 'medium', 'all')]
    [string]$Variant = 'small',
    [string]$Destination,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is much faster without the progress bar
if ($PSVersionTable.PSVersion.Major -lt 6) {
    # Windows PowerShell 5.1 may default to TLS 1.0/1.1, which huggingface.co rejects.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $PSScriptRoot 'models.lock.json'
if (-not $Destination) { $Destination = Join-Path $repoRoot 'models' }
if (-not (Test-Path $lockPath)) { throw "Manifest not found: $lockPath" }

$lock = Get-Content -Raw -Encoding UTF8 $lockPath | ConvertFrom-Json
$entries = @($lock.files | Where-Object { $Variant -eq 'all' -or $_.variant -eq $Variant })
if ($entries.Count -eq 0) { throw "No files in $lockPath match variant '$Variant'." }

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

foreach ($e in $entries) {
    if ($e.revision -notmatch '^[0-9a-f]{40}$') { throw "$($e.repo) has no pinned 40-hex revision in $lockPath." }
    if ($e.sha256 -notmatch '^[0-9a-f]{64}$') { throw "$($e.repo)/$($e.file) has no sha256 in $lockPath." }

    $outDir = Join-Path $Destination $e.model
    $out = Join-Path $outDir $e.file
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    if (-not $Force -and (Test-Path -LiteralPath $out) -and ((Get-Sha256 $out) -eq $e.sha256)) {
        Write-Host "ok       $($e.model)/$($e.file) (already verified)"
        continue
    }

    $url = "https://huggingface.co/$($e.repo)/resolve/$($e.revision)/$($e.file)"
    $tmp = "$out.partial"
    Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
    Write-Host "download $url"
    try {
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -MaximumRetryCount 3 -RetryIntervalSec 2 -ErrorAction Stop
    }
    catch [System.Management.Automation.ParameterBindingException] {
        # Windows PowerShell 5.1 has no retry parameters.
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing -ErrorAction Stop
    }
    catch {
        Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
        throw "Download failed: $url ($($_.Exception.Message))"
    }

    $size = (Get-Item -LiteralPath $tmp).Length
    if ($size -ne [long]$e.size) {
        Remove-Item -LiteralPath $tmp
        throw "SIZE MISMATCH for $($e.model)/$($e.file): expected $($e.size) bytes, got $size."
    }
    $actual = Get-Sha256 $tmp
    if ($actual -ne $e.sha256) {
        Remove-Item -LiteralPath $tmp
        throw "SHA-256 MISMATCH for $($e.model)/$($e.file): expected $($e.sha256), got $actual."
    }
    Move-Item -Force -LiteralPath $tmp -Destination $out
    Write-Host "verified $($e.model)/$($e.file) sha256=$actual"
}

Write-Host "done: $($entries.Count) file(s) in $Destination (license: $($lock.license))"
