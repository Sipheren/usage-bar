@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\scripts\install.ps1" %*
pause
