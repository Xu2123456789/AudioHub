@echo off
rem AudioHub - start the tray app. Just double-click this file.
rem
rem One script serves both builds. The self-contained build carries the .NET
rem runtime, so hostfxr.dll sits right next to the exe; the framework-dependent
rem build has no such file and needs .NET 8 Desktop Runtime installed first.
rem Probing beats shipping two copies of this file, which would drift apart.
rem
rem The Chinese wording lives in show-runtime-help.ps1, not here. A .bat can only
rem be read correctly under one console code page: mixing UTF-8 Chinese into it
rem either shows up as mojibake or, after chcp 65001, desyncs cmd's line offsets
rem and gets fragments of the text executed as commands. PowerShell prints
rem Unicode through WriteConsoleW, so it is unaffected.

if exist "%~dp0hostfxr.dll" goto launch

rem ---- framework-dependent build: look for the .NET 8 desktop runtime ----
set "AH_DOTNET_OK=0"
for /f "delims=" %%i in ('dotnet --list-runtimes 2^>nul ^| findstr /L /C:"Microsoft.WindowsDesktop.App 8"') do set "AH_DOTNET_OK=1"
if "%AH_DOTNET_OK%"=="0" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0show-runtime-help.ps1"
    exit /b 1
)

:launch
cd /d "%~dp0"
start "" "AudioHub.App.exe"
