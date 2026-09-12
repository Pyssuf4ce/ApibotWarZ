@echo off
chcp 65001 >nul
title ApibotWarZ - Build Release Package
cd /d "%~dp0"

echo =======================================================
echo        🚀 ApibotWarZ - Automated Build & Release
echo =======================================================
echo.

:: 1. Compile Go Engine
echo [1/3] 🔨 Compiling Go Bot Engine (ApibotWarZ.exe)...
go build -ldflags="-s -w" -o ApibotWarZ.exe main.go
if %errorlevel% neq 0 (
    echo ❌ Go build failed!
    pause
    exit /b %errorlevel%
)
echo ✅ ApibotWarZ.exe built successfully.
echo.

:: 2. Compile C# UI Launcher (Release + Obfuscation)
echo [2/3] 🔨 Compiling C# UI Launcher (Release + Obfuscar)...
dotnet build UI\ApibotWarZ.UI.csproj -c Release
if %errorlevel% neq 0 (
    echo ❌ Dotnet build failed!
    pause
    exit /b %errorlevel%
)
echo ✅ C# Launcher built and obfuscated successfully.
echo.

:: 3. Assemble Release Distribution Package
echo [3/3] 📦 Assembling Dist_ApibotWarZ Package...
set "DIST_DIR=..\Dist_ApibotWarZ"
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"

:: Clean temporary / debug files from Dist
if exist "%DIST_DIR%\*.pdb" del /f /q "%DIST_DIR%\*.pdb"
if exist "%DIST_DIR%\openvpn.log" del /f /q "%DIST_DIR%\openvpn.log"
if exist "%DIST_DIR%\*.ovpn" del /f /q "%DIST_DIR%\*.ovpn"

:: Copy UI runtime binaries
copy /y UI\bin\Release\net10.0-windows10.0.19041.0\ApibotWarZ.Launcher.exe "%DIST_DIR%\" >nul
copy /y UI\bin\Release\net10.0-windows10.0.19041.0\ApibotWarZ.Launcher.dll "%DIST_DIR%\" >nul
copy /y UI\bin\Release\net10.0-windows10.0.19041.0\ApibotWarZ.Launcher.runtimeconfig.json "%DIST_DIR%\" >nul
copy /y UI\bin\Release\net10.0-windows10.0.19041.0\ApibotWarZ.Launcher.deps.json "%DIST_DIR%\" >nul
copy /y UI\bin\Release\net10.0-windows10.0.19041.0\Microsoft.Windows.SDK.NET.dll "%DIST_DIR%\" >nul
copy /y UI\bin\Release\net10.0-windows10.0.19041.0\WinRT.Runtime.dll "%DIST_DIR%\" >nul

:: Copy Go and Python binaries
copy /y ApibotWarZ.exe "%DIST_DIR%\" >nul
if exist "server.exe" copy /y server.exe "%DIST_DIR%\" >nul

:: Copy vpn configs & auth
if exist "vpn_auth.txt" copy /y vpn_auth.txt "%DIST_DIR%\" >nul
if exist "vpn_configs" xcopy /e /i /y "vpn_configs" "%DIST_DIR%\vpn_configs" >nul

:: Clean unnecessary pdb files from distribution
if exist "%DIST_DIR%\*.pdb" del /f /q "%DIST_DIR%\*.pdb"

:: Create Zip archive for easy distribution
echo 🗜️ Creating ApibotWarZ_Dist.zip...
if exist "..\ApibotWarZ_Dist.zip" del /f /q "..\ApibotWarZ_Dist.zip"
powershell -NoProfile -Command "Compress-Archive -Path '%DIST_DIR%\*' -DestinationPath '..\ApibotWarZ_Dist.zip' -Force"

echo.
echo =======================================================
echo  🎉 BUILD COMPLETE! Package ready in:
echo     📁 Folder : %~dp0..\Dist_ApibotWarZ\
echo     📦 Zip    : %~dp0..\ApibotWarZ_Dist.zip
echo =======================================================
echo.
pause
