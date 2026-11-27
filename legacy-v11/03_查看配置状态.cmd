@echo off
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 is required. Install it and try again.
  pause
  exit /b 1
)
pwsh.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0tools\V11\Deployment.ps1" -Action Status
if errorlevel 1 echo V11 failed. See docs for the error code.
pause
