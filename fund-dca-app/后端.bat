@echo off
title FundDca Backend :5000
rem 锁定工作目录到本文件所在文件夹（fund-dca-app），双击时工作目录才不会漂移
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
