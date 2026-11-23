# Fills in the demo "shell" plugins for previewing the multi-plugin UI.
# plugin.json files live in <main repo>\.pai\release\demo-plugins\<id>\ (written by hand); this script adds
# a placeholder module.dll (NOT a real DLL: launching a game through it fails, which is also a UI state worth seeing),
# draws icon/background images, zips each plugin, and installs copies into the release app folder.
# ASCII-only on purpose (Windows PowerShell 5 reads BOM-less files as ANSI).
param([switch]$NoInstall)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path -Parent $PSScriptRoot
$mainRepo = Split-Path -Parent ((& git -C $repo rev-parse --path-format=absolute --git-common-dir).Trim())
$release = Join-Path $mainRepo '.pai\release'
$demo = Join-Path $release 'demo-plugins'

function New-Png([string]$path, [int]$w, [int]$h, [string]$text, [System.Drawing.Color]$c1, [System.Drawing.Color]$c2) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 45
    $g.FillRectangle($brush, $rect)
    $fontSize = [Math]::Max(12, [int]($h / 3))
    $font = New-Object System.Drawing.Font 'Segoe UI', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($text, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $w, $h), $format)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($dir in Get-ChildItem $demo -Directory) {
    [System.IO.File]::WriteAllText((Join-Path $dir.FullName 'module.dll'), "placeholder module for UI preview - not a real DLL`n")
}
New-Png (Join-Path $demo 'ext.demo.shell\icon.png') 256 256 'S' ([System.Drawing.Color]::FromArgb(255, 99, 102, 241)) ([System.Drawing.Color]::FromArgb(255, 236, 72, 153))
New-Png (Join-Path $demo 'ext.demo.shell\background.png') 1920 1080 'DEMO' ([System.Drawing.Color]::FromArgb(255, 15, 23, 42)) ([System.Drawing.Color]::FromArgb(255, 88, 28, 135))

foreach ($dir in Get-ChildItem $demo -Directory) {
    $zip = Join-Path $demo ($dir.Name + '.zip')
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $dir.FullName -DestinationPath $zip -CompressionLevel Optimal
}

if (-not $NoInstall) {
    $plugins = Join-Path $release 'Sparxie\plugins'
    New-Item -ItemType Directory -Force $plugins | Out-Null
    foreach ($dir in Get-ChildItem $demo -Directory) {
        $target = Join-Path $plugins $dir.Name
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Copy-Item $dir.FullName $target -Recurse
    }
    Write-Host "Installed into: $plugins"
}
Write-Host "Demo plugins: $demo"
Get-ChildItem $demo | Select-Object Name | Format-Table -HideTableHeaders
