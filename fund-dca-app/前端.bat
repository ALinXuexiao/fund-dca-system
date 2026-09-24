@echo off
title FundDca Frontend :5173
rem 锁定工作目录到本文件所在文件夹（fund-dca-app）
cd /d "%~dp0"

rem 系统 Node 是 v18，而 Vite 8 要求 Node ^=20.19；优先使用便携版 Node 22。
set "NODE22=%LOCALAPPDATA%\Programs\nodejs22\node_modules\node\bin"
if exist "%NODE22%\node.exe" set "PATH=%NODE22%;%PATH%"

echo ============================================
echo  Fund DCA Frontend -^>  http://localhost:5173
echo  Close this window or press Ctrl+C to stop.
echo ============================================
echo.

cd /d "%~dp0web"
node --version
call npm.cmd run dev

echo.
echo [Frontend stopped] Exit code %errorlevel%
pause
