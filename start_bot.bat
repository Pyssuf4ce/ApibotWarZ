@echo off
chcp 65001 >nul

:: ตรวจสอบสิทธิ์ Administrator หากยังไม่มีให้เปิดใหม่แบบ Admin ทันที (ครั้งเดียวตอนเริ่ม)
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -WindowStyle Hidden -Command "Start-Process cmd -ArgumentList '/c cd /d \"%~dp0\" && start_bot.bat' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
echo ==============================================
echo ApibotWarZ - Auto Register Bot (Admin Mode)
echo ==============================================

echo Killing old processes...
taskkill /F /IM server.exe >nul 2>&1
taskkill /F /IM python.exe >nul 2>&1
taskkill /F /IM ApibotWarZ.exe >nul 2>&1
taskkill /F /IM openvpn.exe >nul 2>&1

echo Starting API Server (Optimized Captcha Engine)...
start "Captcha Server" cmd /k "python server.py"

echo Waiting 3 seconds for server to start...
timeout /t 3 /nobreak >nul

echo Starting Main Bot...
start "ApibotWarZ" cmd /k "ApibotWarZ.exe"

echo All services started!
