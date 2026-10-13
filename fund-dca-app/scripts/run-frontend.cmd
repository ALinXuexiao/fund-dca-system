@echo off
rem ===========================================================================
rem Frontend dev-server wrapper (launched hidden by start-app.ps1).
rem The system Node is v18 while Vite 8 needs Node >= 20.19, so npm is run
rem explicitly with the portable Node 22. Putting node22 on PATH and calling
rem npm.cmd is NOT enough: Program Files\nodejs\npm.cmd hardcodes its own
rem sibling node.exe (v18).
rem ===========================================================================
set "NODE22=%LOCALAPPDATA%\Programs\nodejs22\node_modules\node\bin"
set "NPM_CLI=C:\Program Files\nodejs\node_modules\npm\bin\npm-cli.js"
set "NODE_EXE=node"
if exist "%NODE22%\node.exe" set "NODE_EXE=%NODE22%\node.exe"

cd /d "%~dp0..\web"

echo [run-frontend] workdir=%CD%
"%NODE_EXE%" --version

if exist "%NPM_CLI%" goto use_npm_cli
echo [run-frontend] npm-cli.js not found, falling back to npm.cmd
call npm.cmd run dev
goto after_run

:use_npm_cli
"%NODE_EXE%" "%NPM_CLI%" run dev

:after_run
echo [run-frontend] frontend exited, ExitCode=%errorlevel%
