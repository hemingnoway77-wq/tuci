@echo off
chcp 65001 >nul
setlocal
set "TUCI_PWSH="
for %%P in (pwsh.exe) do set "TUCI_PWSH=%%~$PATH:P"
if not defined TUCI_PWSH if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "TUCI_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined TUCI_PWSH if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" set "TUCI_PWSH=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
if not defined TUCI_PWSH (
  echo PowerShell 7 is required. Install PowerShell 7 and try again.
  pause
  exit /b 1
)
"%TUCI_PWSH%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish-update-interactive.ps1"
if errorlevel 1 pause
