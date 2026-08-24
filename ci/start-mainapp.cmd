@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-mainapp.ps1" %*
if errorlevel 1 (
    echo.
    echo MainAPP did not start. See the message above.
    pause
)
endlocal