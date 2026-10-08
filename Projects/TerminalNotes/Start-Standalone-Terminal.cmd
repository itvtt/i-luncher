@echo off
setlocal
if not exist "%~dp0artifacts\terminal-desktop-win-x64\TerminalNotes.TerminalDesktop.exe" (
  echo Build the standalone terminal first. See TERMINAL-ENGINE.md.
  pause
  exit /b 1
)
start "" /D "%~dp0." "%~dp0artifacts\terminal-desktop-win-x64\TerminalNotes.TerminalDesktop.exe" %*
