@echo off
title FundDca Frontend :5173
rem Keep the working directory at this file's folder (fund-dca-app).
cd /d "%~dp0"

rem The system Node is v18, but Vite 8 requires Node >= 20.19, so prefer the portable Node 22.
rem The system npm.cmd hardcodes "%~dp0\node.exe" (Node 18), so run npm-cli.js with Node 22 directly.
set "NODE22=%LOCALAPPDATA%\Programs\nodejs22\node_modules\node\bin"
set "NPM_CLI=C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js"
set "HAVE_NODE22="
if exist "%NODE22%\node.exe" if exist "%NPM_CLI%" set "HAVE_NODE22=1"

echo ============================================
echo  Fund DCA Frontend  -^>  http://localhost:5173
echo  Close this window or press Ctrl+C to stop.
echo ============================================
echo.

cd /d "%~dp0web"
if defined HAVE_NODE22 (
  "%NODE22%\node.exe" --version
  "%NODE22%\node.exe" "%NPM_CLI%" run dev
) else (
  echo [warn] portable Node 22 not found, falling back to the system npm.cmd
  call npm.cmd run dev
)

echo.
echo [Frontend stopped] Exit code %errorlevel%
pause
