@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set "IL_EXIT=%ERRORLEVEL%"
if not "%IL_EXIT%"=="0" pause
exit /b %IL_EXIT%
