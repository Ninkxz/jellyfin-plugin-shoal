<#
.SYNOPSIS
  Writes build/out/manifest.json, the manifest of a Jellyfin plugin repository, for a zip that build.ps1 made.
.DESCRIPTION
  The manifest is a JSON array with one entry for the plugin and one version per release (newest first). Pass -Existing to add the new
  version to a manifest you already publish; a version that is listed already is replaced. sourceUrl is a placeholder until the zip has a
  home: -SourceUrl takes the real address.
.PARAMETER Zip
  The zip (default: the newest build/out/shoal_*.zip).
.PARAMETER ImageUrl
  Where the catalog image shoal-plugin.png will be served from (default: a placeholder that is not a real address, like sourceUrl).
.PARAMETER SourceUrl
  Where the zip will be downloaded from (default: a placeholder that is not a real address).
.PARAMETER Existing
  A manifest.json to add this version to.
#>
[CmdletBinding()]
param(
    [string]$Zip,
    [string]$SourceUrl,
    [string]$Changelog,
    [string]$Existing,
    [string]$ImageUrl
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path -Parent $PSScriptRoot
$yaml = Get-BuildYaml (Join-Path $root 'build.yaml')
$out = Join-Path $PSScriptRoot 'out'
if (-not $Zip) {
    $latest = Get-ChildItem -LiteralPath $out -Filter 'shoal_*.zip' -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $latest) { throw 'No zip in build/out; run build/build.ps1 first.' }
    $Zip = $latest.FullName
}
if ($Zip -notmatch 'shoal_(\d+\.\d+\.\d+\.\d+)\.zip$') { throw "The zip name must be shoal_<four-part version>.zip, not '$Zip'." }
$version = $Matches[1]
if (-not $Changelog) { $Changelog = $yaml['changelog'] }
if (-not $SourceUrl) { $SourceUrl = "https://example.invalid/REPLACE-WITH-THE-REAL-ADDRESS/shoal_$version.zip" }
if (-not $ImageUrl) { $ImageUrl = "https://example.invalid/REPLACE-WITH-THE-REAL-ADDRESS/shoal-plugin.png" }

$entry = [ordered]@{
    version   = $version
    changelog = $Changelog
    targetAbi = $yaml['targetAbi']
    sourceUrl = $SourceUrl
    checksum  = (Get-FileHash -LiteralPath $Zip -Algorithm MD5).Hash.ToLowerInvariant()
    timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$versions = @($entry)
if ($Existing) {
    $old = @(Get-Content -LiteralPath $Existing -Raw | ConvertFrom-Json)
    $plugin = $old | Where-Object { $_.guid -eq $yaml['guid'] } | Select-Object -First 1
    if ($plugin) {
        $versions += @($plugin.versions | Where-Object { $_.version -ne $version })
    }
}

$manifest = @([ordered]@{
        guid        = $yaml['guid']
        name        = $yaml['name']
        description = $yaml['description']
        overview    = $yaml['overview']
        owner       = $yaml['owner']
        category    = $yaml['category']
        imageUrl    = $ImageUrl
        versions    = $versions
    })

New-Item -ItemType Directory -Force -Path $out | Out-Null
$path = Join-Path $out 'manifest.json'
$json = ConvertTo-Json -InputObject $manifest -Depth 6
[System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "wrote $path"
Write-Host "checksum $($entry.checksum)"
if ($SourceUrl -like '*example.invalid*') { Write-Warning 'sourceUrl is still the placeholder; pass -SourceUrl before you publish.' }
if ($ImageUrl -like '*example.invalid*') { Write-Warning 'imageUrl is still the placeholder; pass -ImageUrl (the release asset shoal-plugin.png) before you publish.' }
