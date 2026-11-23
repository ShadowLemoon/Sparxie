# Local build to a FIXED folder: <main repo>\.pai\release\
#   Sparxie\                      unpacked app (Sparxie.exe + app-<version>\)
#   Sparxie-desktop-win-x64.zip   the same app, zipped, fixed file name
#   plugins\                      plugin packages copied from sibling plugin repos (if built)
#   _cache\zzz-runtime\           prepared ZZZ runtime (kept between builds)
# The folder is git-ignored and overwritten on every run. Close a running Sparxie from that folder first.
# ASCII-only on purpose (Windows PowerShell 5 reads BOM-less files as ANSI).
#
# Usage: powershell -File build/build_local.ps1 [-Version 0.1.0-dev] [-PluginRepo D:\Code\endfiled-sparxie]
param(
    [string]$Version = '',
    [string]$PluginRepo = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$common = (& git -C $repo rev-parse --path-format=absolute --git-common-dir).Trim()
$mainRepo = Split-Path -Parent $common
$release = Join-Path $mainRepo '.pai\release'
$out = Join-Path $release 'out'
$runtime = Join-Path $release '_cache\zzz-runtime'
$sha = (& git -C $repo rev-parse --short HEAD).Trim()
# unique per build (commit + time) so the new app-<version> folder never collides with one that is still running
# prefixed so no identifier is all digits with a leading zero (invalid in SemVer / NuGet: restore fails right after midnight)
if (-not $Version) { $Version = "0.1.0-dev.g$sha.t" + (Get-Date -Format "HHmmss") }


if (-not (Test-Path (Join-Path $runtime 'ZZZTouchCore.dll'))) {
    throw "ZZZ runtime not prepared: $runtime`nRun: python build/package_runtime.py download-runtime --destination zzz-runtime.zip ; python build/package_runtime.py prepare-runtime --archive zzz-runtime.zip --destination `"$runtime`""
}

New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter '*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force

& (Join-Path $PSScriptRoot 'package_desktop.ps1') -Version $Version -RuntimeDirectory $runtime -OutputDirectory $out
if ($LASTEXITCODE -ne 0) { throw 'package_desktop.ps1 failed' }

# Fixed names at the top of the release folder. A NEW app-<version>\ folder is ADDED next to the old ones and version.ini is
# switched to it; old folders (possibly in use by a running Sparxie) are not touched - the shell launcher cleans them up later.
# Everything else (Sparxie.exe, config.json, plugins\, plugin-trust.json, logs\, cache\ ...) is left alone; Sparxie.exe is only copied when missing.
$app = Join-Path $release 'Sparxie'
$built = Join-Path $out 'Sparxie'
New-Item -ItemType Directory -Force $app | Out-Null
Get-ChildItem $built -Directory -Filter 'app-*' | Move-Item -Destination $app -Force
Copy-Item (Join-Path $built 'version.ini') (Join-Path $app 'version.ini') -Force
if (-not (Test-Path (Join-Path $app 'Sparxie.exe'))) { Copy-Item (Join-Path $built 'Sparxie.exe') $app }
$zip = Get-ChildItem $out -Filter 'Sparxie-desktop-*.zip' | Select-Object -First 1
$fixedZip = Join-Path $release 'Sparxie-desktop-win-x64.zip'
Move-Item $zip.FullName $fixedZip -Force
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

# plugin packages (built by the plugin repos' own scripts)
if (-not $PluginRepo) { $PluginRepo = Join-Path (Split-Path -Parent $mainRepo) 'endfiled-sparxie' }
$plugins = Join-Path $release 'plugins'
New-Item -ItemType Directory -Force $plugins | Out-Null
$pluginDist = Join-Path $PluginRepo 'dist'
if (Test-Path $pluginDist) {
    Get-ChildItem $pluginDist -Filter '*.zip' | Copy-Item -Destination $plugins -Force
    Get-ChildItem $pluginDist -Directory | ForEach-Object {
        $target = Join-Path $plugins $_.Name
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item $_.FullName $target -Recurse
    }
}

Write-Host "Version : $Version"
Write-Host "App     : $app"
Write-Host "Zip     : $fixedZip"
Write-Host ("SHA-256 : " + (Get-FileHash $fixedZip -Algorithm SHA256).Hash)
Write-Host "Plugins : $plugins"
