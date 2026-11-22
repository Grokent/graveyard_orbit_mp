@echo off
REM Graveyard Orbit Co-op updater.
REM Double-click to download and install the latest GitHub release.
REM Extra arguments are passed through: --force, --game-dir "<path>"
setlocal
cd /d "%~dp0"

where powershell >nul 2>nul
if errorlevel 1 (
  echo PowerShell was not found on this system.
  echo Download the latest release manually:
  echo   https://github.com/Grokent/graveyard_orbit_mp/releases/latest
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
  echo Update did not complete. See the message above.
) else (
  echo Done.
)
pause
exit /b %RC%
