<#
.SYNOPSIS
  Builds the plugin and packs build/out/shoal_<version>.zip (the dll plus meta.json, what a Jellyfin plugin repository serves).
.PARAMETER Version
  Four-part version (default: build.yaml). The dll, the zip name and meta.json all carry it.
.PARAMETER Changelog
  The changelog text for meta.json (default: build.yaml).
.PARAMETER SkipTests
  Do not run the test suite first.
.EXAMPLE
  pwsh build/build.ps1 -Version 1.0.0.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Changelog,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path -Parent $PSScriptRoot
$yaml = Get-BuildYaml (Join-Path $root 'build.yaml')
if (-not $Version) { $Version = $yaml['version'] }
if (-not $Changelog) { $Changelog = $yaml['changelog'] }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version must have four parts (for example 1.0.0.0), not '$Version'." }
foreach ($key in 'guid', 'name', 'targetAbi', 'owner') { if (-not $yaml[$key]) { throw "build.yaml has no $key." } }

$sln = Join-Path $root 'Jellyfin.Shoal.sln'
$versionArgs = @("-p:Version=$Version", "-p:AssemblyVersion=$Version", "-p:FileVersion=$Version")

if (-not $SkipTests) {
    & dotnet test $sln -c Release -warnaserror --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw 'The tests failed; nothing was packed.' }
}
else {
    & dotnet build $sln -c Release -warnaserror --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}

$dll = Join-Path $root 'src/Jellyfin.Plugin.Shoal/bin/Release/net10.0/Jellyfin.Plugin.Shoal.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw "The build produced no $dll." }

$out = Join-Path $PSScriptRoot 'out'
$stage = Join-Path $out 'stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath $dll -Destination $stage

# The catalog image (600 x 300) goes next to the dll; imagePath in meta.json names it, and Jellyfin shows it on the Plugins page.
$image = Join-Path $root 'docs/brand/shoal-plugin.png'
if (-not (Test-Path -LiteralPath $image)) { throw "The plugin image is missing: $image" }
Copy-Item -LiteralPath $image -Destination $stage

$meta = [ordered]@{
    category    = $yaml['category']
    changelog   = $Changelog
    description = $yaml['description']
    guid        = $yaml['guid']
    imagePath   = 'shoal-plugin.png'
    name        = $yaml['name']
    overview    = $yaml['overview']
    owner       = $yaml['owner']
    targetAbi   = $yaml['targetAbi']
    timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    version     = $Version
    status      = 'Active'
    autoUpdate  = $false
    assemblies  = @()
}
$metaPath = Join-Path $stage 'meta.json'
[System.IO.File]::WriteAllText($metaPath, ($meta | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))

$zip = Join-Path $out ("shoal_{0}.zip" -f $Version)
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$md5 = (Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant()
Write-Host "packed $zip"
Write-Host "md5    $md5"
