@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title API 서버 i-api 중지
rem Stops the API server running from this folder and removes its start at Windows login.
if not exist "%~dp0i-api.exe" (
  echo i-api.exe 가 이 폴더에 없습니다.
  goto end
)
start "" /wait "%~dp0i-api.exe" --stop
:end
echo.
pause
