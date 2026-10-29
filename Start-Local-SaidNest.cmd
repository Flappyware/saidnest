@echo off
cd /d "%~dp0"
pwsh -NoProfile -File "%~dp0scripts\windows\start-local.ps1"
pause
