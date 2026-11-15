@echo off
setlocal
title 实用工具箱 - 重新编译

where dotnet >nul 2>&1
if errorlevel 1 (
  echo 未找到 .NET 10 SDK，请先安装再编译。
  pause
  exit /b 1
)

echo 正在编译轻量版...
dotnet publish "%~dp0源码\Toolbox.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:NuGetAudit=false -o "%~dp0publish" --nologo -v q
if errorlevel 1 goto fail
copy /Y "%~dp0publish\Toolbox.exe" "%~dp0实用工具箱 轻量版.exe" >nul
rd /s /q "%~dp0publish" 2>nul

echo 正在编译完整版（自带运行时，体积较大，请稍候）...
dotnet publish "%~dp0源码\Toolbox.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:NuGetAudit=false -o "%~dp0publish_full" --nologo -v q
if errorlevel 1 goto fail
copy /Y "%~dp0publish_full\Toolbox.exe" "%~dp0实用工具箱.exe" >nul
rd /s /q "%~dp0publish_full" 2>nul

echo.
echo 已完成：实用工具箱.exe 与 实用工具箱 轻量版.exe 均已更新。
echo 编译前请先关闭正在运行的程序。
pause
exit /b 0

:fail
echo.
echo 编译失败，请查看上面的错误信息。
pause
exit /b 1
