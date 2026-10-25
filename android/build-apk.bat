@echo off
rem Builds the AudioHub Android sender APK. Just double-click this file.
rem
rem Everything runs off the toolchain in D:\app\Android - nothing needs to be on PATH,
rem and no system-wide JDK or Gradle gets involved.
rem
rem Pass Gradle task names to do something else:
rem     build-apk.bat test
rem     build-apk.bat clean
rem     build-apk.bat clean assembleDebug
rem
rem The debug APK ends up at app\build\outputs\apk\debug\app-debug.apk

setlocal
cd /d "%~dp0"

call "%~dp0toolchain-env.bat"
if errorlevel 1 goto :fail

set "TASKS=%*"
if "%TASKS%"=="" set "TASKS=assembleDebug"

echo Toolchain : %TOOLCHAIN%
echo Tasks     : %TASKS%
echo.

call "%GRADLE%" -p "%CD%" %TASKS% --console=plain
set "RESULT=%ERRORLEVEL%"

echo.
if exist "%CD%\app\build\outputs\apk\debug\app-debug.apk" (
    echo APK ready:
    echo   %CD%\app\build\outputs\apk\debug\app-debug.apk
) else (
    echo No APK was produced. Scroll up for the reason.
)

echo.
if not "%RESULT%"=="0" echo Gradle exited with code %RESULT%.
pause
exit /b %RESULT%

:fail
echo.
pause
exit /b 1
