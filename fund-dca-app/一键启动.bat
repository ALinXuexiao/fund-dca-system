@echo off
rem ===========================================================================
rem  FundDca launcher (silent)
rem   - Backend and frontend are started as hidden background processes:
rem     no terminal windows pop up.
rem   - The browser opens as soon as the frontend dev server is ready (~2-3s).
rem     The page shows a skeleton first and fills in data once the backend
rem     (compile + cloud DB) is up.
rem   - Extra args: -Restart   stop running services first
rem                 -NoBrowser do not open the browser
rem  Logs: fund-dca-app\logs\launcher.log / backend.log / frontend.log
rem ===========================================================================
wscript.exe //nologo "%~dp0scripts\launch-hidden.vbs" start %*
exit /b 0
