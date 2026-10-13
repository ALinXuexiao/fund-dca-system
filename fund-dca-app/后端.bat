@echo off
title FundDca Backend :5000
rem Keep the working directory at this file's folder (fund-dca-app) so a double-click cannot drift.
cd /d "%~dp0"

echo ============================================
echo  Fund DCA Backend  -^>  http://localhost:5000
echo  Close this window or press Ctrl+C to stop.
echo ============================================
echo.

dotnet run --project src/FundDca.Api

echo.
echo [Backend stopped] Exit code %errorlevel%
pause
