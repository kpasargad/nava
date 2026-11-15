@echo off
cd /d "%~dp0"
if exist "%~dp0dist\Nava.exe" (
  start "" "%~dp0dist\Nava.exe"
) else (
  echo Please run build.ps1 first.
  pause
)
