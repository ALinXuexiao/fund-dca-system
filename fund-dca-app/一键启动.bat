@echo off
rem ==========================================================================
rem Fund DCA - one-click launcher (backend :5000 + frontend :5173)
rem - Each service runs in its own window (close window = stop service)
rem - A service whose port is already listening is skipped (no duplicate start)
rem - Waits until both are ready, then opens the browser automatically
rem Notes: no parenthesized blocks here, because expanding %PATH% (which
rem contains "Program Files (x86)") inside an IF (...) block breaks cmd.
rem ==========================================================================
cd /d "%~dp0"

set "BE_URL=http://127.0.0.1:5000/health"
set "FE_URL=http://localhost:5173/"
set "FE_PROBE6=http://[::1]:5173/"
set "FE_PROBE4=http://127.0.0.1:5173/"
set "NODE22=%LOCALAPPDATA%\Programs\nodejs22\node_modules\node\bin"

echo [1/4] Checking ports 5000 / 5173 ...
set NEED_BE=1
set NEED_FE=1
netstat -ano | findstr /C:":5000 " | findstr /C:"LISTENING" >nul 2>&1
if not errorlevel 1 set NEED_BE=0
netstat -ano | findstr /C:":5173 " | findstr /C:"LISTENING" >nul 2>&1
if not errorlevel 1 set NEED_FE=0

if %NEED_BE%==0 if %NEED_FE%==0 goto both_running

if %NEED_BE%==1 goto start_be
goto check_fe
:start_be
echo [2/4] Starting backend in a new window (first run may take ~20s) ...
start "FundDca Backend :5000" cmd /k "cd /d %~dp0 && dotnet run --project src/FundDca.Api"

:check_fe
if %NEED_FE%==1 goto start_fe
goto wait_ready
:start_fe
echo [3/4] Starting frontend in a new window ...
start "FundDca Frontend :5173" cmd /k "set PATH=%NODE22%;%PATH% && cd /d %~dp0web && node --version && call npm.cmd run dev"

:wait_ready
echo [4/4] Waiting for services to be ready ...
set /a TRIES=0
:wait_loop
rem ping sleep (~2s); used instead of "timeout" which fails when stdin is redirected
ping -n 3 127.0.0.1 >nul
set /a TRIES+=1
set BE_OK=1
set FE_OK=1
rem body must flow into findstr, so do NOT use "-o nul" here (that would starve the pipe)
curl -s --max-time 3 %BE_URL% 2>nul | findstr /C:"ok" >nul
if not errorlevel 1 set BE_OK=0
rem Vite may bind IPv6 ([::1]) only; probe that first, then fall back to IPv4
curl -s -o nul --max-time 3 "%FE_PROBE6%" >nul 2>&1
if not errorlevel 1 set FE_OK=0
if %FE_OK%==1 curl -s -o nul --max-time 3 "%FE_PROBE4%" >nul 2>&1
if not errorlevel 1 set FE_OK=0
if %BE_OK%==0 if %FE_OK%==0 goto ready
if %TRIES% geq 90 goto failed
goto wait_loop

:both_running
echo       Both services already running.

:ready
echo.
echo ==================================================
echo  READY. Opening %FE_URL%
echo  Backend window : "FundDca Backend :5000"
echo  Frontend window: "FundDca Frontend :5173"
echo  Close those windows to stop the services.
echo ==================================================
start "" %FE_URL%
exit /b 0

:failed
echo.
echo [ERROR] Services did not become ready within 3 minutes.
echo Please check the two service windows for error messages.
pause
exit /b 1
