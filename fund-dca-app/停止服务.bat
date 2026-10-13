@echo off
rem ===========================================================================
rem  Stop FundDca backend + frontend (kills the whole process tree by port)
rem  Ports: 5173 (frontend), 5000 (backend)
rem ===========================================================================
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "scripts\stop-app.ps1"
timeout /t 3 >nul
exit /b 0
