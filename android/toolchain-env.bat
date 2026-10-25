@echo off
rem Shared toolchain setup for the AudioHub Android build scripts.
rem
rem Call it from another script like this - note the deliberate absence of setlocal,
rem because the variables have to survive the call:
rem
rem     setlocal
rem     call "%~dp0toolchain-env.bat"
rem     if errorlevel 1 goto :fail
rem
rem On success it sets TOOLCHAIN, JAVA_HOME, ANDROID_HOME, ANDROID_SDK_ROOT,
rem GRADLE_USER_HOME and GRADLE, and returns errorlevel 0.
rem
rem Deliberately plain cmd: PowerShell scripts are blocked by the default execution
rem policy on a stock Windows install, and a build entry point that only works after
rem the user changes a security setting is not an entry point.

set "TOOLCHAIN=D:\app\Android"
if defined AUDIOHUB_TOOLCHAIN set "TOOLCHAIN=%AUDIOHUB_TOOLCHAIN%"

set "JAVA_HOME=%TOOLCHAIN%\JDK-17"
set "ANDROID_HOME=%TOOLCHAIN%\Android-SDK"
set "ANDROID_SDK_ROOT=%TOOLCHAIN%\Android-SDK"
set "GRADLE_USER_HOME=%TOOLCHAIN%\Gradle-Home"
set "GRADLE=%TOOLCHAIN%\Gradle-8.7\bin\gradle.bat"

if not exist "%JAVA_HOME%\bin\java.exe" (
    echo [toolchain] JDK not found: %JAVA_HOME%
    echo [toolchain] Set AUDIOHUB_TOOLCHAIN if the toolchain lives somewhere else.
    exit /b 1
)

if not exist "%GRADLE%" (
    echo [toolchain] Gradle not found: %GRADLE%
    exit /b 1
)

if not exist "%ANDROID_HOME%\platforms" (
    echo [toolchain] Android SDK platforms not found under: %ANDROID_HOME%\platforms
    exit /b 1
)

if not exist "%GRADLE_USER_HOME%" mkdir "%GRADLE_USER_HOME%"

exit /b 0
