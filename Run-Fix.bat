@echo off
if "%~1"=="" goto usage

dotnet script "%~dp0Fix_SAP_Order_ODBC2039.csx" -- %1
goto end

:usage
echo.
echo  SAP B1 Sales Order Fix - ODBC -2039
echo  =====================================
echo.
echo  USAGE:
echo    Run-Fix.bat docnum:^<DocNum^>     - fix by Sales Order DocNum
echo    Run-Fix.bat trid:^<U_CXS_TRID^>  - fix by U_CXS_TRID field value
echo.
echo  EXAMPLES:
echo    Run-Fix.bat docnum:5054686
echo    Run-Fix.bat trid:TXN-00123
echo.

:end
pause
