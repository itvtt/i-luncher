@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title API 서버 i-api 시작
rem Starts the relay on every IP of this PC (port 2003) with nothing to fill in, registers its hidden start at Windows
rem login and opens the firewall port (one Windows prompt). Run it again after copying in a new relay.py: it restarts.
rem Settings are the SETTINGS at the top of relay.py (no .env). A new relay.py takes the old one's with --settings-from.
call :python || goto end
%PY% relay.py --start
:end
echo.
pause
exit /b

:python
py -3 -c "import sys; sys.exit(sys.version_info < (3, 8))" >nul 2>nul && (set "PY=py -3" & exit /b 0)
python -c "import sys; sys.exit(sys.version_info < (3, 8))" >nul 2>nul && (set "PY=python" & exit /b 0)
echo Python 3.8 이상이 필요합니다. https://www.python.org/downloads/ 에서 설치한 뒤 다시 실행하세요.
exit /b 1
