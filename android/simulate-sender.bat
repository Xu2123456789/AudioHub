@echo off
rem Pretends to be the phone: sends VBAN audio from this PC.
rem
rem Why it exists: before any phone is involved, this is how you find out whether the
rem PC side of the link works. It uses the exact same encoder and sender the phone app
rem uses, so a pass here means the bytes the phone will send are bytes this PC accepts.
rem
rem How to use:
rem   1. Open another window and run dist\probe-vban.bat
rem   2. Double-click this file
rem   probe-vban should report accepted packets well above zero.
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
