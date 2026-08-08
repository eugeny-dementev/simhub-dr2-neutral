@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
    echo.
    echo Build failed. Review the message above.
    pause
    exit /b 1
)
echo.
echo Build complete.
pause
