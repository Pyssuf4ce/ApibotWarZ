@echo off
chcp 65001 >nul

:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% == 0 (
    goto :run_bot
) else (
    echo ⚠️ Requesting Administrator privileges for VPN to work...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

:run_bot
cd /d "%~dp0"
echo ==============================================
echo 🚀 ApibotWarZ - Auto Register Bot
echo ==============================================

echo 📡 กำลังเปิด API Server สำหรับแก้ Captcha...
start "Captcha Server" cmd /c "server.exe"

echo ⏳ รอระบบเซิร์ฟเวอร์พร้อมทำงาน 3 วินาที...
timeout /t 3 /nobreak >nul

echo 🤖 กำลังเปิดระบบบอทหลัก...
start "ApibotWarZ" cmd /k "ApibotWarZ.exe"

echo ✅ เปิดโปรแกรมครบแล้ว!
timeout /t 5 >nul
