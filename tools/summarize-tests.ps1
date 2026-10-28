<#
.SYNOPSIS
  Summarizes TRX test results as Markdown (used in release notes and the job summary).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$ResultsDir,
  [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$rows = foreach ($trx in Get-ChildItem $ResultsDir -Recurse -Filter '*.trx') {
  [xml]$x = Get-Content $trx.FullName -Raw
  $c = $x.TestRun.ResultSummary.Counters
  [pscustomobject]@{
    Suite = [IO.Path]::GetFileNameWithoutExtension($trx.Name)
    Total = [int]$c.total; Passed = [int]$c.passed; Failed = [int]$c.failed
    Skipped = [int]$c.total - [int]$c.executed
    Outcome = $x.TestRun.ResultSummary.outcome
  }
}
$lines = @('### Automated tests (windows-latest)', '', '| Suite | Total | Passed | Failed | Skipped | Outcome |', '|---|---:|---:|---:|---:|---|')
foreach ($r in $rows) { $lines += "| $($r.Suite) | $($r.Total) | $($r.Passed) | $($r.Failed) | $($r.Skipped) | $($r.Outcome) |" }
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
$lines -join "`n" | Set-Content -Path $Output -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { $lines -join "`n" | Add-Content -Path $env:GITHUB_STEP_SUMMARY }
$lines -join "`n"
