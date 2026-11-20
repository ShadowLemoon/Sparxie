# 构建测试用 mock 游戏模块和假游戏：用 Visual Studio 的 cl.exe 直接编译，
# 输出 tests/MockGameModule/bin/mock_game_module.dll 和 bin/Mock.exe。
# 测试按相对路径查找它们；未构建时相关测试自动返回（不失败）。
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$out = Join-Path $here 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$include = Join-Path $here '..\..\native\plugin-abi\include'
cmd /c "`"$vcvars`" && cl /nologo /utf-8 /LD /W4 /I `"$include`" `"$here\mock_game_module.c`" /Fe:`"$out\mock_game_module.dll`" /Fo:`"$out\\`""
if ($LASTEXITCODE -ne 0) { throw "cl.exe 失败（mock_game_module）: $LASTEXITCODE" }
cmd /c "`"$vcvars`" && cl /nologo /utf-8 /W4 `"$here\fake_game.c`" /Fe:`"$out\Mock.exe`" /Fo:`"$out\\`" /link /SUBSYSTEM:CONSOLE"
if ($LASTEXITCODE -ne 0) { throw "cl.exe 失败（fake_game）: $LASTEXITCODE" }
Write-Host "已生成 $out\mock_game_module.dll 和 $out\Mock.exe"
