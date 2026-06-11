@echo off
:: ===========================================================================
::  Detect.bat — SAP B1 ODBC -2039 Proactive Detector
::
::  Queries SQL directly for at-risk Sales Orders (no log files needed).
::  Covers all users and all machines in one central run.
::
::  USAGE:
::    Detect.bat             <- detect and report only (SQL access only needed)
::    Detect.bat autofix     <- detect + fix all via SAP DI API
::    Detect.bat dryrun      <- show what would be fixed, no changes
:: ===========================================================================
setlocal

if "%~1"=="" (
    dotnet script "%~dp0Detect-ODBC2039.csx"
) else (
    dotnet script "%~dp0Detect-ODBC2039.csx" -- %~1
)

pause
