@echo off
rem 启动 AudioHub 验证工具（真正的逻辑在同目录的 audiohub-verify.ps1）。
rem 用户只需双击本文件；中文提示由 PowerShell 输出，避免 cmd 中文乱码。
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0audiohub-verify.ps1"
echo.
echo Done. Press any key to close this window.
pause >nul
endlocal
