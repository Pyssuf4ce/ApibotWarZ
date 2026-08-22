@echo off

net session >nul 2>&1
if %errorLevel% == 0 (
    goto :run_bot
) else (
    echo Requesting Administrator privileges for VPN to work...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

:run_bot
cd /d "%~dp0"
echo ==============================================
echo ApibotWarZ - Auto Register Bot
echo ==============================================

echo Starting API Server...
start "Captcha Server" cmd /c "server.exe"

echo Waiting 3 seconds for server to start...
timeout /t 3 /nobreak >nul

echo Starting Main Bot...
start "ApibotWarZ" cmd /k "ApibotWarZ.exe"

echo Done!
timeout /t 5 >nul
