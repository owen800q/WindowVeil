<#
.SYNOPSIS
  Validates produced NuGet packages: the expected set exists, versions match, and every package
  contains a real assembly, XML documentation and the README (no empty placeholder packages).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$PackageDir,
  [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = @(
  'WindowVeil.Core', 'WindowVeil.Native', 'WindowVeil.Wpf', 'WindowVeil.Automation', 'WindowVeil.Tracking',
  'WindowVeil.Capture', 'WindowVeil.Ocr', 'WindowVeil.Ocr.Paddle', 'WindowVeil.Profiles'
)
$errors = [System.Collections.Generic.List[string]]::new()
foreach ($id in $expected) {
  $path = Join-Path $PackageDir "$id.$Version.nupkg"
  if (-not (Test-Path $path)) { $errors.Add("Missing package $id.$Version.nupkg"); continue }
  $zip = [IO.Compression.ZipFile]::OpenRead($path)
  try {
    $names = $zip.Entries | ForEach-Object FullName
    $dll = $names | Where-Object { $_ -match "^lib/net8\.0(-windows[0-9.]*)?/$([regex]::Escape($id))\.dll$" }
    $xml = $names | Where-Object { $_ -match "^lib/net8\.0(-windows[0-9.]*)?/$([regex]::Escape($id))\.xml$" }
    if (-not $dll) { $errors.Add("$id has no lib/net8.0*/$id.dll") }
    if (-not $xml) { $errors.Add("$id has no XML documentation") }
    if ($names -notcontains 'README.md') { $errors.Add("$id has no README.md") }
    $dllEntry = $zip.Entries | Where-Object FullName -eq ($dll | Select-Object -First 1)
    if ($dllEntry -and $dllEntry.Length -lt 4096) { $errors.Add("$id assembly is suspiciously small ($($dllEntry.Length) bytes)") }
    $nuspec = $zip.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
    $reader = [IO.StreamReader]::new($nuspec.Open())
    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($text -notmatch "<version>$([regex]::Escape($Version))</version>") { $errors.Add("$id nuspec version is not $Version") }
  } finally { $zip.Dispose() }
}
$unexpected = Get-ChildItem $PackageDir -Filter '*.nupkg' | Where-Object { $expected -notcontains ($_.Name -replace "\.$([regex]::Escape($Version))\.nupkg$", '') }
foreach ($u in $unexpected) { $errors.Add("Unexpected package $($u.Name) (test/sample projects must not be packable)") }
if ($errors.Count) { $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }; throw "Package validation failed ($($errors.Count) errors)" }
Write-Host "Validated $($expected.Count) packages at version $Version."
