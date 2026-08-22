@echo off
chcp 65001 >nul
title ApibotWarZ - Build Release Package
cd /d "%~dp0"

echo =======================================================
echo        🚀 ApibotWarZ - Automated Build & Release
echo =======================================================
echo.

:: 1. Compile Go Engine
echo [1/4] 🔨 Compiling Go Bot Engine (ApibotWarZ.exe)...
go build -o ApibotWarZ.exe main.go
if %errorlevel% neq 0 (
    echo ❌ Go build failed!
    pause
    exit /b %errorlevel%
)
echo ✅ ApibotWarZ.exe built successfully.
echo.

:: 2. Compile Python Captcha Server (Onefile)
echo [2/4] 🔨 Compiling Python Captcha Server (server.exe)...
pyinstaller --noconfirm --onefile --console --name "server" server.py
if %errorlevel% neq 0 (
    echo ❌ PyInstaller build failed!
    pause
    exit /b %errorlevel%
)
copy /y dist\server.exe .\server.exe >nul
echo ✅ server.exe built successfully.
echo.

:: 3. Compile C# UI Launcher (Release + Obfuscation)
echo [3/4] 🔨 Compiling C# UI Launcher (Release + Obfuscar)...
dotnet build UI\ApibotWarZ.UI.csproj -c Release
if %errorlevel% neq 0 (
    echo ❌ Dotnet build failed!
    pause
    exit /b %errorlevel%
)
echo ✅ C# Launcher built and obfuscated successfully.
echo.

:: 4. Assemble Release Distribution Package
echo [4/4] 📦 Assembling Launcher_Dist Package...
if not exist "Launcher_Dist" mkdir Launcher_Dist

copy /y UI\bin\Release\net10.0-windows10.0.19041.0\* Launcher_Dist\ >nul
copy /y ApibotWarZ.exe Launcher_Dist\ >nul
copy /y server.exe Launcher_Dist\ >nul
copy /y Start_UI.bat Launcher_Dist\ >nul
copy /y server.py Launcher_Dist\ >nul
if exist "vpn_auth.txt" copy /y vpn_auth.txt Launcher_Dist\ >nul

:: Clean unnecessary pdb files from distribution
if exist "Launcher_Dist\*.pdb" del /f /q "Launcher_Dist\*.pdb" >nul

:: Create Zip archive for easy distribution
echo 🗜️ Creating ApibotWarZ_Release.zip...
powershell -Command "Compress-Archive -Path 'Launcher_Dist\*' -DestinationPath 'ApibotWarZ_Release.zip' -Force"

echo.
echo =======================================================
echo  🎉 BUILD COMPLETE! Package ready in:
echo     📁 Folder : %~dp0Launcher_Dist\
echo     📦 Zip    : %~dp0ApibotWarZ_Release.zip
echo =======================================================
echo.
pause
