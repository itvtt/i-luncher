@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title API 서버 i-api 시작
rem Starts the API server (i-api.exe) hidden, registers its start at Windows login and opens the firewall port (one Windows prompt).
rem Run it again after copying in a new version: it restarts. Settings are settings.txt beside i-api.exe (made on the first start).
if not exist "%~dp0i-api.exe" (
  echo i-api.exe 가 이 폴더에 없습니다. i-luncher 릴리스의 i-api-win-x64.zip 을 이 폴더에 푸세요.
  goto end
)
start "" /wait "%~dp0i-api.exe" --start
:end
echo.
pause
