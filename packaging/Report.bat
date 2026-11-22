@echo off
setlocal
cd /d "%~dp0"

echo.
echo  Graveyard Orbit Co-op - collecting diagnostics...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Report.ps1"

if errorlevel 1 (
  echo.
  echo  Report failed. You can still send the folder named
  echo  GraveyardOrbit-Co-op-Report-* on your Desktop.
)

echo.
pause
