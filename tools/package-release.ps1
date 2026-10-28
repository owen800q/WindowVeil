<#
.SYNOPSIS
  Packs the WindowVeil NuGet packages, publishes the self-contained demo and writes SHA256SUMS.
.DESCRIPTION
  Output (artifacts/release):
    WindowVeil.<Package>.<version>.nupkg   one per implemented library (empty projects are never packed)
    WindowVeil-Demo-win-x64.zip            self-contained .NET 8 x64 demo
    WindowVeil-Editor-win-x64.zip          self-contained .NET 8 x64 visual editor
    SHA256SUMS                             sha256 of every file above
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Version,
  [string]$Configuration = 'Release',
  [string]$OutputDir = (Join-Path $PSScriptRoot '..' 'artifacts' 'release'),
  [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$packArgs = @('pack', (Join-Path $root 'WindowVeil.sln'), '-c', $Configuration, '-o', $OutputDir, "-p:Version=$Version")
if ($NoBuild) { $packArgs += '--no-build' }
dotnet @packArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed' }

& (Join-Path $PSScriptRoot 'validate-packages.ps1') -PackageDir $OutputDir -Version $Version

foreach ($app in @(
    @{ Project = 'samples/WindowVeil.Demo/WindowVeil.Demo.csproj'; Zip = 'WindowVeil-Demo-win-x64.zip' },
    @{ Project = 'samples/WindowVeil.Editor/WindowVeil.Editor.csproj'; Zip = 'WindowVeil-Editor-win-x64.zip' })) {
  $publishDir = Join-Path $root 'artifacts' 'publish' ([IO.Path]::GetFileNameWithoutExtension($app.Zip))
  if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
  dotnet publish (Join-Path $root $app.Project) -c $Configuration -r win-x64 --self-contained true -o $publishDir "-p:Version=$Version"
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($app.Project)" }
  Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath (Join-Path $OutputDir $app.Zip) -CompressionLevel Optimal
}

$sums = Get-ChildItem $OutputDir -File | Where-Object Name -ne 'SHA256SUMS' | Sort-Object Name | ForEach-Object {
  '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.Name
}
# LF line endings so `sha256sum -c` works on Linux runners.
[IO.File]::WriteAllText((Join-Path $OutputDir 'SHA256SUMS'), (($sums -join "`n") + "`n"))
Get-Content (Join-Path $OutputDir 'SHA256SUMS')
