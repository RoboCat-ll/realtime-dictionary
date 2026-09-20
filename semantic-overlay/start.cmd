@echo off
setlocal
set "SCRIPT=%~dp0start.ps1"

where pwsh.exe >nul 2>nul
if errorlevel 1 goto use_windows_powershell

pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
goto check_result

:use_windows_powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"

:check_result
if errorlevel 1 goto start_failed
echo.
echo Realtime Dictionary is ready. You can close this window.
exit /b 0

:start_failed
echo.
echo Realtime Dictionary failed to start. See _native_host.log.
pause
exit /b 1
