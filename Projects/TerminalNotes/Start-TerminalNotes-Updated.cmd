@echo off
setlocal
if not exist "%~dp0artifacts\win-x64-clean-ui\TerminalNotes.exe" (
  echo TerminalNotes.exe was not found. Follow the publish instructions in README.md.
  pause
  exit /b 1
)
start "" /D "%~dp0." "%~dp0artifacts\win-x64-clean-ui\TerminalNotes.exe"
