# Package the Sparxie desktop app in the versioned-app-folder layout:
#
#   Sparxie\
#     Sparxie.exe            <- tiny shell launcher (native, no runtime needed)
#     version.ini            <- exe_path=app-<version>\Sparxie.Desktop.exe
#     app-<version>\         <- everything else (Desktop + Broker + SessionHost + native DLLs + runtime + licenses)
#     (config.json, desktop.json, logs\, cache\ are created next to Sparxie.exe at run time)
#
# Usage:
#   powershell -File build/package_desktop.ps1 -Version 0.1.0-preview.5 -RuntimeDirectory <prepared zzz runtime dir> -OutputDirectory <dir>
# Prepare the runtime first:
#   python build/package_runtime.py download-runtime --destination zzz-runtime.zip
#   python build/package_runtime.py prepare-runtime --archive zzz-runtime.zip --destination <RuntimeDirectory>
param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [Parameter(Mandatory = $true)] [string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory,
    [string]$HoyoTouchCore = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $HoyoTouchCore) { $HoyoTouchCore = Join-Path $repo 'native/HoyoTouchCore/build/Release/HoyoTouchCore.dll' }
if (-not (Test-Path $HoyoTouchCore)) { throw "HoyoTouchCore.dll not found: $HoyoTouchCore" }
foreach ($f in 'ZZZTouchCore.dll', 'ZZZTouchRuntime.dll') {
    if (-not (Test-Path (Join-Path $RuntimeDirectory $f))) { throw "Runtime file missing: $f (run prepare-runtime first)" }
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$root = Join-Path $OutputDirectory 'Sparxie'
$app = Join-Path $root "app-$Version"
$work = Join-Path $OutputDirectory '_publish'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app | Out-Null

foreach ($project in 'Sparxie.SessionHost', 'Sparxie.Broker', 'Sparxie.Desktop') {
    $out = Join-Path $work $project
    dotnet publish (Join-Path $repo "src/$project/$project.csproj") -c Release -r win-x64 --self-contained -p:Platform=x64 -p:Version=$Version -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
    Copy-Item (Join-Path $out '*') $app -Recurse -Force
}

Copy-Item $HoyoTouchCore (Join-Path $app 'HoyoTouchCore.dll') -Force
# EndfieldCore/EndfieldRuntime come from the SessionHost publish (native/EndfieldCore must be built first)
foreach ($f in 'EndfieldCore.dll', 'EndfieldRuntime.dll') { if (-not (Test-Path (Join-Path $app $f))) { throw "$f missing from the publish output (build native/EndfieldCore first)" } }
foreach ($f in 'ZZZTouchCore.dll', 'ZZZTouchRuntime.dll') { Copy-Item (Join-Path $RuntimeDirectory $f) $app -Force }

$notices = @(
    @((Join-Path $repo 'LICENSE'), 'LICENSE'),
    @((Join-Path $repo 'THIRD-PARTY-NOTICES.md'), 'THIRD-PARTY-NOTICES.md'),
    @((Join-Path $repo 'native/HoyoTouchCore/upstream/LICENSE'), 'UPSTREAM-LICENSE-MIT.txt'),
    @((Join-Path $repo 'native/HoyoTouchCore/adapter/licenses/inih-LICENSE.txt'), 'inih-LICENSE.txt'),
    @((Join-Path $repo 'docs/RUNTIME-NOTICE.md'), 'RUNTIME-NOTICE.md')
)
foreach ($n in $notices) { Copy-Item $n[0] (Join-Path $app $n[1]) -Force }

Get-ChildItem $app -Recurse -File -Include *.pdb, workloads*.json | Remove-Item -Force

& (Join-Path $PSScriptRoot 'build-shell.ps1') -OutputDirectory $root
if ($LASTEXITCODE -ne 0) { throw 'shell build failed' }
[System.IO.File]::WriteAllText((Join-Path $root 'version.ini'), "exe_path=app-$Version\Sparxie.Desktop.exe`r`n")

$zip = Join-Path $OutputDirectory "Sparxie-desktop-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $root -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $work -Recurse -Force
Write-Host "Package folder: $root"
Write-Host "Package zip:    $zip"
(Get-FileHash $zip -Algorithm SHA256).Hash
