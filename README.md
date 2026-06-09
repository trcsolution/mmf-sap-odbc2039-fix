# SAP B1 – Sales Order & Purchase Order ODBC -2039 Fix

## Problem

When opening a Sales Order or Purchase Order in SAP Business One, the following error appears:

> **"Another user or another operation modified data; to continue, open the window again '' (RDR1) (ODBC -2039)"**

### Root Cause

The error is caused by a phantom **committed quantity** in the `OITW` (warehouse stock) table
that no longer has a matching open document. Specifically:

- `OITW.IsCommited` has a non-zero value for an item/warehouse that has no open lines
- The Sales Order's `LogInstanc` field is mismatched against the internal audit history (`ADOC`)
- SAP's optimistic locking detects the inconsistency and blocks any save attempt

---

## Fix Overview

The fix re-opens the Sales Order or Purchase Order through the **SAP B1 DI API** and calls `Update()`.
This forces SAP's engine to re-evaluate and reconcile the `OITW` committed quantities,
clearing the phantom lock — without any manual SQL updates to stock tables.

### Files

| File | Description |
|------|-------------|
| `Detect-ODBC2039.csx` | **Proactive** SQL-based detector — finds at-risk orders before users hit the error; optional `-- autofix` mode |
| `Schedule-Detect.ps1` | Registers a daily Windows Scheduled Task to auto-fix at-risk orders |
| `Detect.bat` | Batch launcher for `Detect-ODBC2039.csx` |
| `Fix_SAP_Order_ODBC2039.csx` | Targeted fix — connects via DI API and updates a specific order |
| `config.json` | Connection settings — edit this when deploying to a new environment |
| `Run-Fix.bat` | Batch launcher for `Fix_SAP_Order_ODBC2039.csx` |
| `Setup.ps1` | Prerequisite installer — run once on a new machine |
| `Scan-SAPLogs-ODBC2039.csx` | Reactive — scans local SAP log files for past -2039 errors |
| `Scan-Logs.bat` | Batch launcher for `Scan-SAPLogs-ODBC2039.csx` |
| `Check_LogInstanc.sql` | SQL diagnostic — inspect LogInstanc mismatches in SSMS |

---

## Proactive Detection (Recommended)

> **TL;DR** — Don't wait for users to call support. Run `Detect.bat autofix` daily
> (or schedule it with `Schedule-Detect.ps1`) to catch and fix affected orders automatically.

The `Detect-ODBC2039.csx` script queries the SAP database **directly** for the exact condition
that causes ODBC -2039, without relying on SAP client log files.

### Why SQL detection beats log scanning

| | `Scan-SAPLogs` (reactive) | `Detect-ODBC2039` (proactive) |
|---|---|---|
| **Timing** | After user hits the error | Before any user is affected |
| **Scope** | One machine's log files | Entire company database, one SQL query |
| **Machine-specific** | Yes — must run on each workstation | No — runs from any machine with SQL access |
| **SAP client required** | Yes | Only for `-- autofix` mode |
| **Schedulable centrally** | No | Yes — register once with `Schedule-Detect.ps1` |

### How detection works

The root cause of `(RDR1)(ODBC -2039)` is a **`LogInstanc` mismatch**: the live `RDR1` table
has an older `LogInstanc` than the latest `ADOC` audit snapshot for the same document.
SAP's optimistic concurrency check detects this when a user tries to save, and blocks with -2039.

The detect script runs this SQL check directly:

```sql
-- Open orders where RDR1.LogInstanc != max(ADOC.LogInstanc) → will throw ODBC -2039
SELECT o.DocEntry, o.DocNum, ...
FROM ORDR o
JOIN RDR1 r  ON r.DocEntry = o.DocEntry
JOIN (SELECT DocEntry, MAX(LogInstanc) AS MaxADOC
      FROM ADOC WHERE ObjType = '17' GROUP BY DocEntry) a
  ON a.DocEntry = o.DocEntry
WHERE o.DocStatus = 'O'
  AND r.LogInstanc != a.MaxADOC
```

### Usage

```bat
Detect.bat             <- report only (SQL access only, no SAP client needed)
Detect.bat autofix     <- detect + fix all via SAP DI API
Detect.bat dryrun      <- show what would be fixed, no changes
```

Or via VS Code: **Ctrl+Shift+P** → **Tasks: Run Task** → `SAP: Detect at-risk orders (...)`

Output is saved to `ODBC2039_AtRisk.csv`.

### Schedule automatic daily auto-fix

Run **once** as Administrator on the machine that will act as the scheduler:

```powershell
powershell -ExecutionPolicy Bypass -File Schedule-Detect.ps1
```

This registers a Windows Scheduled Task (`SAP-ODBC2039-AutoFix`) that runs
`Detect-ODBC2039.csx -- autofix` every day at **06:00**, before business hours.
Logs are appended to `Logs\detect.log`.

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Time 05:30` | `06:00` | Daily run time (24-hour) |
| `-DryRun` | off | Register in report-only mode |
| `-Remove` | — | Unregister the scheduled task |
| `-Force` | — | Overwrite an existing task |

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
  "DiServer":      "DevServer",
  "SqlServer":     "DevServer\\SQL20",
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
| `SAP Fix: default DocNum (5054686)` | Runs immediately for the default Sales Order |
| `SAP Fix: by DocNum (prompt)` | Prompts for a Sales Order DocNum |
| `SAP Fix: by U_CXS_TRID (prompt)` | Prompts for a Sales Order `U_CXS_TRID` value |
| `SAP Fix: PO by DocNum (prompt)` | Prompts for a Purchase Order DocNum |
| `SAP Fix: PO by U_CXS_TRID (prompt)` | Prompts for a Purchase Order `U_CXS_TRID` value |

### Option 2 — Batch file (`Run-Fix.bat`)

```bat
Run-Fix.bat                         ← default DocNum 5054686 (Sales Order)
Run-Fix.bat docnum:5054686          ← Sales Order by DocNum
Run-Fix.bat trid:TXN-00123          ← Sales Order by U_CXS_TRID
Run-Fix.bat po-docnum:5054686       ← Purchase Order by DocNum
Run-Fix.bat po-trid:TXN-00123       ← Purchase Order by U_CXS_TRID
```

### Option 3 — Direct command line

**Sales Orders:**
```powershell
dotnet script Fix_SAP_Order_ODBC2039.csx
dotnet script Fix_SAP_Order_ODBC2039.csx -- docnum:5054686
dotnet script Fix_SAP_Order_ODBC2039.csx -- trid:TXN-00123
```

**Purchase Orders:**
```powershell
dotnet script Fix_SAP_Order_ODBC2039.csx -- po-docnum:5054686
dotnet script Fix_SAP_Order_ODBC2039.csx -- po-trid:TXN-00123
```

---

## Expected Output (success)

```
Order type  : Sales Order
Lookup mode : DOCNUM
Lookup value: 5054686
Resolved to DocEntry 445884
Connecting to SAP_MURPHY on DevServer ...
Connected OK.
Sales Order retrieved: DocEntry=445884  DocNum=5054686  Status=0
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
| SAP DI Server (`DiServer`) | `DevServer` |
| SQL Server (`SqlServer`) | `DevServer\SQL20` |
| Database | `SAP_MURPHY` |
| License Server | `localhost:30000` |
| SLD Server | `DevServer:40000` |
| DI API COM ProgID | `SAPbobsCOM.Company` |
