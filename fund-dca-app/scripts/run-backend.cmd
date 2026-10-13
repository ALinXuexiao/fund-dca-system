@echo off
rem ===========================================================================
rem Backend service wrapper (launched hidden by start-app.ps1).
rem Can also be run directly for debugging (ASCII-only file: cmd reads it as ANSI).
rem ===========================================================================
cd /d "%~dp0.."

echo [run-backend] workdir=%CD%
echo [run-backend] dotnet run --project src\FundDca.Api
dotnet run --project src\FundDca.Api
echo [run-backend] backend exited, ExitCode=%errorlevel%
