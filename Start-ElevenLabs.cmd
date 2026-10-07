@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Start-Backend.ps1" -ProjectDirectory "%~dp0."
if errorlevel 1 (
  pause
  exit /b 1
)
if exist "%~dp0Launcher\ScamWYF.Launcher.exe" (
  start "" "%~dp0Launcher\ScamWYF.Launcher.exe"
) else if exist "%~dp0dist\Launcher\ScamWYF.Launcher.exe" (
  start "" "%~dp0dist\Launcher\ScamWYF.Launcher.exe"
)
