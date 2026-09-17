@echo off
chcp 65001 > nul
title FastLogin Suite - Release Builder
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_release.ps1"
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] An error occurred during the build process.
    pause
)
