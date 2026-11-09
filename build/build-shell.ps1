# Build the Sparxie shell launcher (root Sparxie.exe, a tiny static-CRT native exe).
# Usage: powershell -File build/build-shell.ps1 -OutputDirectory <目录>
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repo 'src/Sparxie.Shell/Sparxie.Shell.cpp'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere not found: install Visual Studio with the C++ toolset.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'MSVC C++ toolset not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out = Join-Path (Resolve-Path $OutputDirectory) 'Sparxie.exe'
$obj = Join-Path ([System.IO.Path]::GetTempPath()) 'sparxie-shell-obj'
New-Item -ItemType Directory -Force -Path $obj | Out-Null

$cmd = "`"$vcvars`" >nul && cl /nologo /std:c++20 /O2 /MT /EHsc /utf-8 /Fo`"$obj\\`" /Fe`"$out`" `"$source`" /link /SUBSYSTEM:WINDOWS"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Shell launcher build failed (exit code $LASTEXITCODE)" }
Write-Host "Shell launcher built: $out"
