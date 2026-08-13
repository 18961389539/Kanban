@echo off
rem ============================================================
rem  One-click start: PlcSimulator + Kanban.Collector + MainAPP
rem  Kills running instances first, rebuilds (Debug), then starts.
rem  Usage:
rem    start_env.bat              kill old -> build -> start
rem    start_env.bat -SkipBuild   use existing binaries (faster)
rem    start_env.bat -KeepServices  keep nssm/sc services untouched
rem ============================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start_env.ps1" %*
if errorlevel 1 (
    echo.
    echo Startup failed. See output above.
    pause
)
endlocal
