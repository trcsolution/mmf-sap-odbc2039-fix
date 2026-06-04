# SAP B1 – Sales Order ODBC -2039 Fix

## Problem

When opening a Sales Order in SAP Business One, the following error appears:

> **"Another user or another operation modified data; to continue, open the window again '' (RDR1) (ODBC -2039)"**

### Root Cause

The error is caused by a phantom **committed quantity** in the `OITW` (warehouse stock) table
that no longer has a matching open document. Specifically:

- `OITW.IsCommited` has a non-zero value for an item/warehouse that has no open lines
- The Sales Order's `LogInstanc` field is mismatched against the internal audit history (`ADOC`)
- SAP's optimistic locking detects the inconsistency and blocks any save attempt

---

## Fix Overview

The fix re-opens the Sales Order through the **SAP B1 DI API** and calls `Update()`.
This forces SAP's engine to re-evaluate and reconcile the `OITW` committed quantities,
clearing the phantom lock — without any manual SQL updates to stock tables.

### Files

| File | Description |
|------|-------------|
| `Fix_SAP_Order_ODBC2039.csx` | C# script (dotnet-script) — connects via DI API and updates the order |
| `config.json` | Connection settings — edit this when deploying to a new environment |
| `Run-Fix.bat` | Batch launcher — pass a DocNum or TRID as argument |
| `Setup.ps1` | Prerequisite installer — run once on a new machine |
| `Scan-SAPLogs-ODBC2039.ps1` | Scans SAP log files for -2039 errors, exports found orders to `ODBC2039_Found.csv` |
| `Check_LogInstanc.sql` | SQL diagnostic — inspect LogInstanc mismatches in SSMS |
| `sql.sql` | Ad-hoc SQL queries used during investigation |

---

## Requirements

Run `Setup.ps1` on a new machine — it checks and installs everything automatically:

```powershell
powershell -ExecutionPolicy Bypass -File Setup.ps1
```

Or manually:

- **.NET SDK 6+** — https://dotnet.microsoft.com/download
- **dotnet-script** global tool:
  ```
  dotnet tool install -g dotnet-script
  ```
- **SAP B1 DI API** installed locally on the machine running the script  
  (`SAPbobsCOM100.dll` at `C:\Program Files\SAP\SAP Business One DI API\`)  
  — the COM class `SAPbobsCOM.Company` must be registered
- Network access to the SQL Server and SAP License/SLD servers (see Connection Details below)

---

## Configuration

All connection details are stored in **`config.json`** — no credentials are hardcoded in the script.
Edit this file when deploying to a different environment:

```json
{
  "Server":        "DevServer\\SQL20",
  "CompanyDB":     "SAP_MURPHY",
  "UserName":      "manager",
  "Password":      "MMSap2023!",
  "DbUserName":    "sa",
  "DbPassword":    "Password1",
  "DefaultDocNum": 5054686,
  "DbServerType":  17,
  "LicenseServer": "localhost:30000",
  "SLDServer":     "DevServer:40000"
}
```

> ⚠️ `config.json` contains credentials — do not commit it to source control.

---

## How to Run

### Option 1 — VS Code Tasks (recommended)

Press **Ctrl+Shift+P** → **Tasks: Run Task**, then choose:

| Task | Description |
|------|-------------|
| `SAP Fix: default DocNum (5054686)` | Runs immediately for the default order |
| `SAP Fix: by DocNum (prompt)` | Prompts for a DocNum |
| `SAP Fix: by U_CXS_TRID (prompt)` | Prompts for a `U_CXS_TRID` value |

### Option 2 — Batch file (`Run-Fix.bat`)

```bat
Run-Fix.bat                    ← default DocNum 5054686
Run-Fix.bat docnum:5054686     ← by SAP DocNum
Run-Fix.bat trid:TXN-00123     ← by U_CXS_TRID (UDF field on Sales Order)
```

### Option 3 — Direct command line

```powershell
dotnet script Fix_SAP_Order_ODBC2039.csx
dotnet script Fix_SAP_Order_ODBC2039.csx -- docnum:5054686
dotnet script Fix_SAP_Order_ODBC2039.csx -- trid:TXN-00123
```

---

## Expected Output (success)

```
Lookup mode : DOCNUM
Lookup value: 5054686
Resolved to DocEntry 445884
Connecting to SAP_MURPHY on DevServer\SQL20 ...
Connected OK.
Order retrieved: DocEntry=445884  DocNum=5054686  Status=0
Calling Update() ...

SUCCESS!  Sales Order 445884 (DocNum 5054686) updated via DI API.
SAP has re-evaluated the order. The ODBC -2039 lock should now be cleared.
```

---

## SAP Log Files

If the script fails or SAP returns an unexpected error, check the SAP B1 client logs:

```
%LocalAppData%\SAP\SAP Business One\Log\BusinessOne
```

Full path example:
```
C:\Users\<username>\AppData\Local\SAP\SAP Business One\Log\BusinessOne
```

The most relevant log files for DI API issues:
- `b1-diapi-*.log` — DI API connection and operation errors
- `b1-client-*.log` — general SAP B1 client errors

---

## Connection Details

| Setting | Value |
|---------|-------|
| SQL Server | `DevServer\SQL20` |
| Database | `SAP_MURPHY` |
| License Server | `localhost:30000` |
| SLD Server | `DevServer:40000` |
| DI API COM ProgID | `SAPbobsCOM.Company` |
