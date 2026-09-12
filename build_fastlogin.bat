@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FastLoginSuite_Login_Source\build_release.ps1"