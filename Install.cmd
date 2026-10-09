@echo off
setlocal
title ZeroDS - Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0InstallWizard.ps1"
set "INSTALL_RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %INSTALL_RESULT%
