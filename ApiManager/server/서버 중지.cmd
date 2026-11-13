@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title Terminal Notes 중계 서버 중지
rem Stops the relay running from this folder and removes its start at Windows login.
call :python || goto end
%PY% relay.py --stop
:end
echo.
pause
exit /b

:python
py -3 -c "import sys; sys.exit(sys.version_info < (3, 8))" >nul 2>nul && (set "PY=py -3" & exit /b 0)
python -c "import sys; sys.exit(sys.version_info < (3, 8))" >nul 2>nul && (set "PY=python" & exit /b 0)
echo Python 3.8 이상이 필요합니다. https://www.python.org/downloads/ 에서 설치한 뒤 다시 실행하세요.
exit /b 1
