# 编译原生右键菜单扩展（直接调用 cl.exe，不依赖 cmd）
$ErrorActionPreference = 'Stop'
$root = 'D:\软件\自己做的\右键卸载\ShellExtNative'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "未找到含 C++ 工具集的 VS 安装" }
Write-Host "VS: $vsPath"

$msvc = Get-ChildItem "$vsPath\VC\Tools\MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $msvc) { throw "缺少 MSVC 工具集" }

# SDK 路径从注册表读，各机器不一定在 C 盘
$sdkReg = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft SDKs\Windows\v10.0" -ErrorAction SilentlyContinue
$sdkRoot = if ($sdkReg -and $sdkReg.InstallationFolder) { $sdkReg.InstallationFolder.TrimEnd('\') } else { "C:\Program Files (x86)\Windows Kits\10" }
$sdkVer = Get-ChildItem "$sdkRoot\Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdkVer) { throw "缺少 Windows SDK" }
Write-Host ("MSVC: " + $msvc.Name + "  SDK: " + $sdkVer.Name + "  SDKRoot: " + $sdkRoot)

$env:PATH = "$($msvc.FullName)\bin\Hostx64\x64;" + $env:PATH
$env:INCLUDE = @(
    "$($msvc.FullName)\include",
    "$sdkRoot\Include\$($sdkVer.Name)\ucrt",
    "$sdkRoot\Include\$($sdkVer.Name)\um",
    "$sdkRoot\Include\$($sdkVer.Name)\shared"
) -join ';'
$env:LIB = @(
    "$($msvc.FullName)\lib\x64",
    "$sdkRoot\Lib\$($sdkVer.Name)\ucrt\x64",
    "$sdkRoot\Lib\$($sdkVer.Name)\um\x64"
) -join ';'

Set-Location $root
& cl.exe /nologo /LD /EHsc /O2 /std:c++17 /utf-8 /MT /DUNICODE /D_UNICODE /W3 `
    UninstallCommand.cpp `
    /link /DEF:exports.def /OUT:ToolboxShellNative.dll `
    ole32.lib shell32.lib shlwapi.lib user32.lib advapi32.lib

if ($LASTEXITCODE -ne 0) { throw "编译失败，exit=$LASTEXITCODE" }
Get-Item "$root\ToolboxShellNative.dll" | Select-Object Name,@{n='KB';e={[math]::Round($_.Length/1KB,1)}}
