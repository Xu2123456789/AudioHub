@echo off
rem Pretends to be the phone: sends VBAN audio from this PC.
rem
rem Why it exists: before any phone is involved, this is how you find out whether the
rem PC side of the link works. It uses the exact same encoder and sender the phone app
rem uses, so a pass here means the bytes the phone will send are bytes this PC accepts.
rem
rem How to use (paths below are relative to the repo root, not this android\ folder):
rem   1. In another window start a VBAN listener, e.g.
rem        dist\AudioHub.Cli.exe --vban --seconds 90
rem      or just double-click dist\audiohub-verify.bat (it listens for 90 s).
rem      There is no probe-vban.bat any more; the CLI does it directly.
rem   2. Double-click this file
rem   The listener should report accepted packets well above zero.
rem
rem Extra options go straight through, e.g.:
rem   simulate-sender.bat --seconds=60
rem   simulate-sender.bat --host=192.168.1.5 --port=6980
rem   simulate-sender.bat --silent

setlocal
cd /d "%~dp0"

call "%~dp0toolchain-env.bat"
if errorlevel 1 goto :fail

set "APPARGS=%*"

echo Toolchain  : %TOOLCHAIN%
echo Sender args: %APPARGS%
echo.

if "%APPARGS%"=="" (
    call "%GRADLE%" -p "%CD%" :simulator:run --console=plain
) else (
    call "%GRADLE%" -p "%CD%" :simulator:run "--args=%APPARGS%" --console=plain
)
set "RESULT=%ERRORLEVEL%"

echo.
if not "%RESULT%"=="0" echo Gradle exited with code %RESULT%.
pause
exit /b %RESULT%

:fail
echo.
pause
exit /b 1
