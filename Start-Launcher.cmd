@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Launcher.ps1"
if errorlevel 1 goto launch_failed
exit /b 0

:launch_failed
pause
exit /b 1
