@echo off
rem ===========================================================================
rem  AudioHub - open the two UDP ports the phone needs (run once, as admin)
rem ===========================================================================
rem
rem  WHY THE OLD VERSION WAS NOT ENOUGH
rem
rem    It created ONE rule, for UDP 6980 only, and tied it to a single program
rem    path ("program=...\dist\AudioHub.Cli.exe"). Two things went wrong:
rem
rem      1. There was NO rule for UDP 6979 at all. That is the discovery port,
rem         so "Find PC" on the phone could never get an answer - and Windows
rem         drops the packet silently, so nothing anywhere looks broken.
rem      2. Because the rule was tied to one .exe path, running the other build
rem         (dist-self-contained vs dist) or moving the folder bypassed it.
rem
rem  WHAT IT DOES NOW
rem
rem    1. Deletes every old AudioHub rule, including the "Block" rules Windows
rem       writes when a firewall prompt is cancelled. Block rules win over
rem       allow rules, so they have to go first.
rem    2. Adds two allow rules that are NOT tied to a program path:
rem         UDP 6980 - audio from the phone
rem         UDP 6979 - discovery, so the phone can find this PC by itself
rem       Port-based rules keep working after you move or rename the folder.
rem
rem  SCOPE
rem
rem    remoteip=LocalSubnet means only devices on the same Wi-Fi / hotspot can
rem    reach these ports. Nothing is exposed to the internet.
rem
rem  HOW TO RUN
rem
rem    Just double-click. Windows asks for administrator rights - click Yes.
rem ===========================================================================

setlocal
cd /d "%~dp0"

net session >nul 2>&1
if not errorlevel 1 goto :HasAdmin

echo.
echo   Administrator rights are required. Asking Windows to elevate...
echo   If a "User Account Control" window appears, click Yes.
echo.
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
if not errorlevel 1 exit /b 0

echo.
echo   Could not elevate automatically.
echo   Right-click "allow-firewall.bat" and choose "Run as administrator".
echo.
pause
exit /b 1

:HasAdmin

echo.
echo [1/4] Removing old AudioHub rules (block rules included)...
netsh advfirewall firewall delete rule name="AudioHub.Cli" >nul 2>&1
netsh advfirewall firewall delete rule name="AudioHub VBAN input (UDP 6980)" >nul 2>&1
netsh advfirewall firewall delete rule name="AudioHub audio (UDP 6980)" >nul 2>&1
netsh advfirewall firewall delete rule name="AudioHub discovery (UDP 6979)" >nul 2>&1
echo       done

echo [2/4] Allowing inbound UDP 6980 (audio) from the local subnet...
netsh advfirewall firewall add rule name="AudioHub audio (UDP 6980)" description="Lets the AudioHub phone app send audio to this PC." dir=in action=allow protocol=UDP localport=6980 remoteip=LocalSubnet profile=any enable=yes >nul
if errorlevel 1 goto :Failed

echo [3/4] Allowing inbound UDP 6979 (discovery) from the local subnet...
netsh advfirewall firewall add rule name="AudioHub discovery (UDP 6979)" description="Lets the AudioHub phone app find this PC without typing an address." dir=in action=allow protocol=UDP localport=6979 remoteip=LocalSubnet profile=any enable=yes >nul
if errorlevel 1 goto :Failed

echo [4/4] Verifying...
echo.
echo --- these two rules must be listed, both "Allow" ---
echo.
netsh advfirewall firewall show rule name="AudioHub audio (UDP 6980)"
echo.
netsh advfirewall firewall show rule name="AudioHub discovery (UDP 6979)"
echo.
echo --- old block rules must be gone ---
netsh advfirewall firewall show rule name="AudioHub.Cli"
echo.
echo   Done. Both ports are open to the local network only.
echo   Go back to the verify window and press Enter to continue.
echo.
pause
exit /b 0

:Failed
echo.
echo   FAILED to add a firewall rule (see the error above).
echo   Right-click this file and choose "Run as administrator", then retry.
echo.
pause
exit /b 1
