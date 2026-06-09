@echo off
if "%~1"=="" goto usage

dotnet script "%~dp0Fix_SAP_Order_ODBC2039.csx" -- %1
goto end

:usage
echo.
echo  SAP B1 Sales Order ^& Purchase Order Fix - ODBC -2039
echo  =====================================================
echo.
echo  USAGE (Sales Orders):
echo    Run-Fix.bat docnum:^<DocNum^>        - fix Sales Order by DocNum
echo    Run-Fix.bat trid:^<U_CXS_TRID^>     - fix Sales Order by U_CXS_TRID value
echo.
echo  USAGE (Purchase Orders):
echo    Run-Fix.bat po-docnum:^<DocNum^>     - fix Purchase Order by DocNum
echo    Run-Fix.bat po-trid:^<U_CXS_TRID^>  - fix Purchase Order by U_CXS_TRID value
echo.
echo  EXAMPLES:
echo    Run-Fix.bat docnum:5054686
echo    Run-Fix.bat trid:TXN-00123
echo    Run-Fix.bat po-docnum:5054686
echo    Run-Fix.bat po-trid:TXN-00123
echo.

:end
pause

