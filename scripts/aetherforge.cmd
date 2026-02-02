@echo off
setlocal
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0aetherforge.ps1" %*
exit /b %errorlevel%
