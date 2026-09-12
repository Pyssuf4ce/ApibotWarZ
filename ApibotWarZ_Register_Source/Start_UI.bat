@echo off
cd /d "%~dp0"

if exist "%~dp0ApibotWarZ.Launcher.exe" (
    start "" "%~dp0ApibotWarZ.Launcher.exe"
) else if exist "%~dp0Launcher_Dist\ApibotWarZ.Launcher.exe" (
    start "" "%~dp0Launcher_Dist\ApibotWarZ.Launcher.exe"
) else (
    start "" "%~dp0UI\bin\Release\net10.0-windows10.0.19041.0\ApibotWarZ.Launcher.exe"
)
