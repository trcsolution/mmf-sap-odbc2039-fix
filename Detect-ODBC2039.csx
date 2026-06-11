#!/usr/bin/env dotnet-script
//==============================================================================
//  Detect-ODBC2039.csx
//
//  Proactively detects open Sales Orders at risk of ODBC -2039 by reconciling
//  committed stock (OITW.IsCommited) against the real open demand and then
//  probing each candidate order via the DI API.
//
//  Unlike Scan-SAPLogs-ODBC2039.csx, this script:
//    - Requires NO SAP client log files
//    - Runs from ANY machine with SQL access (no SAP client needed)
//    - Covers ALL users and ALL workstations in one query
//    - Can find affected orders BEFORE any user encounters the error
//    - Can be scheduled to auto-fix orders nightly
//
//  HOW IT WORKS (evidence-based, validated against the live database):
//
//    The LogInstanc theory was DISPROVEN: in this database every RDR1 row has
//    LogInstanc = 0, so an "RDR1 vs ADOC LogInstanc mismatch" matches ~84% of
//    all open orders and is therefore useless as a signal.
//
//    The real signal is a phantom COMMITTED quantity in OITW: the order's items
//    show OITW.IsCommited HIGHER than the sum of all genuine open demand
//    (open sales orders + transfer requests + production components + reserve
//    invoices). Calling Update() forces SAP to recalculate OITW and clears the
//    inconsistency, which is exactly what resolves the ODBC -2039 lock.
//
//    Two-stage detection:
//      Stage 1 (SQL)     — pre-filter open orders that contain an over-committed
//                          (phantom) item. Cheap, non-invasive, runs anywhere.
//      Stage 2 (DI API)  — probe each candidate with a harmless Comments toggle
//                          + Update(). Confirms AND fixes -2039 in one pass.
//
//  USAGE:
//    dotnet script Detect-ODBC2039.csx                  <- detect & report only
//    dotnet script Detect-ODBC2039.csx -- autofix       <- detect + fix all via DI API
//    dotnet script Detect-ODBC2039.csx -- dryrun        <- detect + preview (no changes)
//
//  SCHEDULING:
//    Run Schedule-Detect.ps1 once as Administrator to register a Windows
//    Scheduled Task that auto-fixes at-risk orders daily before business hours.
//==============================================================================

#r "nuget: Microsoft.Data.SqlClient, 5.2.1"

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

// ── Load config.json ──────────────────────────────────────────────────────────
var configPath = Path.Combine(Environment.CurrentDirectory, "config.json");
if (!File.Exists(configPath))
{
    Console.WriteLine($"ERROR: config.json not found at {configPath}");
    Environment.Exit(1);
}

var cfg          = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
string SqlServer     = cfg.GetProperty("SqlServer").GetString()!;
string CompanyDB     = cfg.GetProperty("CompanyDB").GetString()!;
string UserName      = cfg.GetProperty("UserName").GetString()!;
string Password      = cfg.GetProperty("Password").GetString()!;
string DbUserName    = cfg.GetProperty("DbUserName").GetString()!;
string DbPassword    = cfg.GetProperty("DbPassword").GetString()!;
int    DbSrvType     = cfg.GetProperty("DbServerType").GetInt32();
string LicenseServer = cfg.GetProperty("LicenseServer").GetString()!;
string SLDServer     = cfg.GetProperty("SLDServer").GetString()!;

// ── Parse arguments ───────────────────────────────────────────────────────────
bool autoFix = Args.Any(a => a.Trim().Equals("autofix", StringComparison.OrdinalIgnoreCase));
bool dryRun  = Args.Any(a => a.Trim().Equals("dryrun",  StringComparison.OrdinalIgnoreCase));

// ── Banner ────────────────────────────────────────────────────────────────────
string modeLabel = dryRun    ? "DRY RUN (no changes)"
                 : autoFix   ? "DETECT + AUTO-FIX"
                             : "DETECT / REPORT ONLY";
Console.WriteLine();
Console.WriteLine("  SAP B1 — ODBC -2039 Proactive Detector");
Console.WriteLine("  ========================================");
Console.WriteLine($"  Database  : {CompanyDB} on {SqlServer}");
Console.WriteLine($"  Mode      : {modeLabel}");
Console.WriteLine($"  Run time  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine();

// ── SQL Stage 1: pre-filter open orders containing an over-committed item ────
//
//  Phantom = OITW.IsCommited - (open SO + open transfer requests +
//                               open production component demand +
//                               open A/R reserve invoices)
//
//  A positive Phantom means SAP has reserved (committed) more stock than any
//  live document justifies — the fingerprint of an order that will throw -2039.
//  We use a #temp table in a single batch so the heavy reconciliation is
//  materialised once and the join to ORDR/RDR1 stays fast.
const string DetectSql = @"
    SELECT
        w.ItemCode, w.WhsCode,
        CAST(w.IsCommited - (ISNULL(so.Q,0)+ISNULL(tr.Q,0)+ISNULL(pr.Q,0)+ISNULL(ri.Q,0))
             AS DECIMAL(19,6)) AS Phantom
    INTO #Phantom
    FROM OITW w
    LEFT JOIN (SELECT ItemCode, WhsCode, SUM(OpenQty) Q FROM RDR1
               WHERE LineStatus='O' GROUP BY ItemCode, WhsCode) so
           ON so.ItemCode=w.ItemCode AND so.WhsCode=w.WhsCode
    LEFT JOIN (SELECT ItemCode, FromWhsCod WhsCode, SUM(OpenQty) Q FROM WTQ1
               WHERE LineStatus='O' GROUP BY ItemCode, FromWhsCod) tr
           ON tr.ItemCode=w.ItemCode AND tr.WhsCode=w.WhsCode
    LEFT JOIN (SELECT c.ItemCode, c.wareHouse WhsCode, SUM(c.PlannedQty-c.IssuedQty) Q
               FROM WOR1 c JOIN OWOR h ON h.DocEntry=c.DocEntry
               WHERE h.Status IN ('R','P') GROUP BY c.ItemCode, c.wareHouse) pr
           ON pr.ItemCode=w.ItemCode AND pr.WhsCode=w.WhsCode
    LEFT JOIN (SELECT i.ItemCode, i.WhsCode, SUM(i.OpenQty) Q FROM INV1 i
               JOIN OINV h ON h.DocEntry=i.DocEntry
               WHERE h.isIns='Y' AND i.LineStatus='O' GROUP BY i.ItemCode, i.WhsCode) ri
           ON ri.ItemCode=w.ItemCode AND ri.WhsCode=w.WhsCode
    WHERE w.IsCommited <> 0;

    SELECT
        o.DocEntry,
        o.DocNum,
        ISNULL(o.U_CXS_TRID, '')              AS TRID,
        CONVERT(VARCHAR(10), o.DocDate, 120)  AS DocDate,
        o.CardCode,
        ISNULL(o.CardName, '')                AS CardName,
        COUNT(DISTINCT r.ItemCode)            AS PhantomItems,
        CAST(MAX(p.Phantom) AS DECIMAL(19,6)) AS MaxPhantom
    FROM ORDR o
    JOIN RDR1 r       ON r.DocEntry = o.DocEntry AND r.LineStatus = 'O'
    JOIN #Phantom p   ON p.ItemCode = r.ItemCode AND p.WhsCode = r.WhsCode AND p.Phantom > 0
    WHERE o.DocStatus = 'O' AND o.Canceled = 'N'
    GROUP BY o.DocEntry, o.DocNum, o.U_CXS_TRID, o.DocDate, o.CardCode, o.CardName
    ORDER BY o.DocNum;";

// Tuple type to hold one at-risk (candidate) order row
var atRisk = new List<(int DocEntry, int DocNum, string Trid, string DocDate,
                        string CardCode, string CardName, int PhantomItems,
                        decimal MaxPhantom)>();

string connStr = $"Server={SqlServer};Database={CompanyDB};User Id={DbUserName};Password={DbPassword};TrustServerCertificate=True;";

Console.Write("  Reconciling committed stock and pre-filtering orders ... ");
try
{
    using var conn = new SqlConnection(connStr);
    conn.Open();
    using var cmd = new SqlCommand(DetectSql, conn) { CommandTimeout = 180 };
    using var rdr = cmd.ExecuteReader();
    while (rdr.Read())
    {
        atRisk.Add((
            rdr.GetInt32(0), rdr.GetInt32(1),
            rdr.GetString(2), rdr.GetString(3),
            rdr.GetString(4), rdr.GetString(5),
            rdr.GetInt32(6), rdr.GetDecimal(7)));
    }
}
catch (Exception ex)
{
    Console.WriteLine($"SQL ERROR: {ex.Message}");
    Environment.Exit(1);
}

Console.WriteLine($"{atRisk.Count} found.");
Console.WriteLine();

// ── All clear ────────────────────────────────────────────────────────────────
if (atRisk.Count == 0)
{
    Console.WriteLine("  ✓ No at-risk Sales Orders detected. System is clean.");
    Console.WriteLine();
    Environment.Exit(0);
}

// ── Print findings table ──────────────────────────────────────────────────────
Console.WriteLine($"  \u26a0  {atRisk.Count} candidate order(s) with phantom committed stock:");
Console.WriteLine();
Console.WriteLine($"  {"DocEntry",-10} {"DocNum",-10} {"DocDate",-12} {"CardCode",-14} {"Phantom",-9} {"Items",-6} TRID");
Console.WriteLine($"  {new string('-', 82)}");
foreach (var r in atRisk)
    Console.WriteLine($"  {r.DocEntry,-10} {r.DocNum,-10} {r.DocDate,-12} {r.CardCode,-14} {r.MaxPhantom,-9:0.##} {r.PhantomItems,-6} {r.Trid}");
Console.WriteLine();

// ── Export CSV (initial — status = Pending) ───────────────────────────────────
string csvPath = Path.Combine(Environment.CurrentDirectory, "ODBC2039_AtRisk.csv");

void WriteCsv(List<(int DocEntry, int DocNum, string Trid, string DocDate,
                     string CardCode, string CardName, int PhantomItems, decimal MaxPhantom,
                     string Status, string Detail)> rows)
{
    using var csv = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
    csv.WriteLine("DocEntry,DocNum,TRID,DocDate,CardCode,CardName,PhantomItems,MaxPhantomQty,FixStatus,Detail");
    foreach (var row in rows)
        csv.WriteLine($"{row.DocEntry},{row.DocNum},{row.Trid},{row.DocDate},{row.CardCode}," +
                      $"\"{row.CardName}\",{row.PhantomItems},{row.MaxPhantom:0.##},{row.Status},{row.Detail}");
}

WriteCsv(atRisk.Select(r => (r.DocEntry, r.DocNum, r.Trid, r.DocDate,
                              r.CardCode, r.CardName, r.PhantomItems, r.MaxPhantom,
                              "Pending", "")).ToList());
Console.WriteLine($"  Report saved : {csvPath}");
Console.WriteLine();

// ── Dry-run mode ──────────────────────────────────────────────────────────────
if (dryRun)
{
    Console.WriteLine($"  [DRY RUN] Would auto-fix {atRisk.Count} order(s) via DI API — no changes made.");
    Console.WriteLine($"            Re-run with  -- autofix  to apply fixes.");
    Console.WriteLine();
    Environment.Exit(0);
}

// ── Report-only mode ──────────────────────────────────────────────────────────
if (!autoFix)
{
    Console.WriteLine("  Options:");
    Console.WriteLine("    Detect.bat autofix   <- fix all orders above via DI API");
    Console.WriteLine("    Detect.bat dryrun    <- preview what would be fixed");
    Console.WriteLine("    Run-Fix.bat docnum:<DocNum>  <- fix a single order");
    Console.WriteLine();
    Environment.Exit(0);
}

// ── Auto-fix: connect to DI API and fix each order ────────────────────────────
Console.WriteLine($"  Connecting to SAP DI API ({SqlServer}) ...");

dynamic company = Activator.CreateInstance(Type.GetTypeFromProgID("SAPbobsCOM.Company"))
    ?? throw new Exception("Could not create SAPbobsCOM.Company COM object. Is SAP DI API installed?");

company.Server        = SqlServer;
company.CompanyDB     = CompanyDB;
company.UserName      = UserName;
company.Password      = Password;
company.DbUserName    = DbUserName;
company.DbPassword    = DbPassword;
company.DbServerType  = DbSrvType;
company.UseTrusted    = false;
company.LicenseServer = LicenseServer;
company.SLDServer     = SLDServer;

int connRet = company.Connect();
if (connRet != 0)
{
    Console.WriteLine($"  CONNECTION FAILED. Code={company.GetLastErrorCode()}  {company.GetLastErrorDescription()}");
    Environment.Exit(1);
}
Console.WriteLine("  DI API connected OK.");
Console.WriteLine();

int cntFixed = 0, cntFailed = 0, cntSkipped = 0;
var results = new List<(int DocEntry, int DocNum, string Trid, string DocDate,
                         string CardCode, string CardName, int PhantomItems, decimal MaxPhantom,
                         string Status, string Detail)>();
int idx = 0;

foreach (var r in atRisk)
{
    idx++;
    Console.Write($"  [{idx}/{atRisk.Count}] DocEntry {r.DocEntry} (DocNum {r.DocNum}) phantom={r.MaxPhantom:0.##} ... ");

    dynamic order = company.GetBusinessObject(17); // boOrders

    if (!(bool)order.GetByKey(r.DocEntry))
    {
        string err = $"{company.GetLastErrorCode()} {company.GetLastErrorDescription()}";
        Console.WriteLine($"FAILED  (GetByKey: {err})");
        results.Add((r.DocEntry, r.DocNum, r.Trid, r.DocDate, r.CardCode, r.CardName,
                     r.PhantomItems, r.MaxPhantom, "FAILED", $"GetByKey: {err}"));
        cntFailed++;
        continue;
    }

    if ((int)order.DocumentStatus != 0)
    {
        Console.WriteLine("SKIPPED (order not open)");
        results.Add((r.DocEntry, r.DocNum, r.Trid, r.DocDate, r.CardCode, r.CardName,
                     r.PhantomItems, r.MaxPhantom, "SKIPPED", "Document status is not Open"));
        cntSkipped++;
        continue;
    }

    // Toggle Comments to create a dirty flag, forcing SAP to recalc OITW commitments
    string comments = (string)order.Comments ?? "";
    order.Comments = comments.EndsWith(".") ? comments.TrimEnd('.') : comments + ".";

    int ret = order.Update();
    if (ret != 0)
    {
        int errCode    = company.GetLastErrorCode();
        string errMsg  = company.GetLastErrorDescription();
        string status  = errCode == -2039 ? "STILL_LOCKED" : "FAILED";
        Console.WriteLine($"{status}  ({errCode}: {errMsg})");
        results.Add((r.DocEntry, r.DocNum, r.Trid, r.DocDate, r.CardCode, r.CardName,
                     r.PhantomItems, r.MaxPhantom, status, $"{errCode}: {errMsg}"));
        cntFailed++;
    }
    else
    {
        Console.WriteLine("FIXED \u2713");
        results.Add((r.DocEntry, r.DocNum, r.Trid, r.DocDate, r.CardCode, r.CardName,
                     r.PhantomItems, r.MaxPhantom, "FIXED", ""));
        cntFixed++;
    }
}

company.Disconnect();
Console.WriteLine();
Console.WriteLine($"  DI API disconnected.");
Console.WriteLine($"  ─────────────────────────────────");
Console.WriteLine($"  Fixed   : {cntFixed}");
Console.WriteLine($"  Failed  : {cntFailed}");
Console.WriteLine($"  Skipped : {cntSkipped}");
Console.WriteLine();

// ── Update CSV with final results ─────────────────────────────────────────────
WriteCsv(results);
Console.WriteLine($"  Results saved: {csvPath}");
Console.WriteLine();

if (cntFailed > 0)
{
    Console.WriteLine("  Some orders could not be fixed.");
    Console.WriteLine("  STILL_LOCKED: a blocking SQL session may be holding a lock — retry during off-peak hours.");
    Console.WriteLine("  FAILED: check DI API error code and review the order in SAP B1.");
    Console.WriteLine();
}

Environment.Exit(cntFailed > 0 ? 1 : 0);
