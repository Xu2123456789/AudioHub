@echo off
rem AudioHub - create Start Menu + Desktop shortcuts (run once).
rem After this you can launch AudioHub via Win-key search instead of digging through files.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-shortcut.ps1"
echo.
pause
