@echo off
if "%~1"=="" goto scan_all

:: Accept both "20260610" and "from:20260610"
set ARG=%~1
if /i "%ARG:~0,5%"=="from:" goto run_with_arg
set ARG=from:%~1

:run_with_arg
dotnet script "%~dp0Scan-SAPLogs-ODBC2039.csx" -- %ARG%
goto end

:scan_all
dotnet script "%~dp0Scan-SAPLogs-ODBC2039.csx"

:end
pause
