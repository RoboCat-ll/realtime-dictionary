@echo off
setlocal
set "SCRIPT=%~dp0configure-key.ps1"
where pwsh.exe >nul 2>&1
if %errorlevel%==0 (
  pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
)
if errorlevel 1 (
  echo API Key 配置失败。
  pause
  exit /b 1
)
pause
