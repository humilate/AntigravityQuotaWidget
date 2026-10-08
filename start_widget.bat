@echo off
cd /d "%~dp0dist"
taskkill /f /im AntigravityQuotaWidget.exe >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1
start "" "%~dp0dist\AntigravityQuotaWidget.exe"
