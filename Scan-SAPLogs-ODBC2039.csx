#!/usr/bin/env dotnet-script
//==============================================================================
//  Scan-SAPLogs-ODBC2039.csx
//
//  Scans SAP B1 client log files for ODBC -2039 errors on Sales Orders,
//  extracts DocEntry values, looks up DocNum + U_CXS_TRID from SQL,
//  and exports results to ODBC2039_Found.csv next to this script.
//
//  USAGE:
//    dotnet script Scan-SAPLogs-ODBC2039.csx                        <- all log files
//    dotnet script Scan-SAPLogs-ODBC2039.csx -- from:20260530        <- since date (yyyyMMdd)
//==============================================================================


#r "nuget: Microsoft.Data.SqlClient, 5.2.1"

using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

// --- Load config.json --------------------------------------------------------
var configPath = Path.Combine(Environment.CurrentDirectory, "config.json");
if (!File.Exists(configPath))
{
    Console.WriteLine($"ERROR: config.json not found at {configPath}");
    Environment.Exit(1);
}

var cfg = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
string sqlServer  = cfg.GetProperty("Server").GetString()!;
string sqlDb      = cfg.GetProperty("CompanyDB").GetString()!;
string sqlUser    = cfg.GetProperty("DbUserName").GetString()!;
string sqlPass    = cfg.GetProperty("DbPassword").GetString()!;

// --- Parse optional 'from:yyyyMMdd' argument --------------------------------
DateTime? fromDate = null;
if (Args.Count > 0)
{
    var arg = Args[0].Trim();
    if (arg.StartsWith("from:", StringComparison.OrdinalIgnoreCase))
    {
        var dateStr = arg.Substring(5).Trim();
        if (DateTime.TryParseExact(dateStr, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed))
        {
            fromDate = parsed;
            Console.WriteLine($"  Date filter : from {fromDate:dd/MM/yyyy} (yyyyMMdd: {dateStr})");
        }
        else
        {
            Console.WriteLine($"ERROR: Invalid date format '{dateStr}'. Use yyyyMMdd, e.g. from:20260530");
            Environment.Exit(1);
        }
    }
}

// --- Log folder --------------------------------------------------------------
string logFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    @"SAP\SAP Business One\Log\BusinessOne");

if (!Directory.Exists(logFolder))
{
    Console.WriteLine($"ERROR: SAP log folder not found: {logFolder}");
    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"  Scanning: {logFolder}");
Console.WriteLine();

// --- Scan log files ----------------------------------------------------------
// Files are named *.log.csv, encoded UTF-16 LE.
// Pattern per error block (consecutive lines):
//   Line N:   "Update failed, err number is -2039" + "BO=Sales Orders" + "BOID=<DocEntry>"
//   Line N+1: "(RDR1).*(ODBC -2039)"
var reRdr1    = new Regex(@"\(RDR1\).*\(ODBC -2039\)", RegexOptions.IgnoreCase);
var reBoid    = new Regex(@"BO=Sales Orders[^\d]*BOID=(\d+)", RegexOptions.IgnoreCase);

var docEntries = new HashSet<int>();

// Regex to extract date from filename: e.g. Client.b1logger.20260603_162404.pid30104.log.csv
var reFileDate = new Regex(@"(\d{8})_\d{6}", RegexOptions.IgnoreCase);

foreach (var file in Directory.EnumerateFiles(logFolder, "*.csv", SearchOption.AllDirectories))
{
    // Filter by date embedded in filename
    if (fromDate.HasValue)
    {
        var fm = reFileDate.Match(Path.GetFileName(file));
        if (fm.Success &&
            DateTime.TryParseExact(fm.Groups[1].Value, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var fileDate))
        {
            if (fileDate < fromDate.Value)
                continue;
        }
    }

    string[] lines;
    try { lines = File.ReadAllLines(file, System.Text.Encoding.Unicode); }
    catch { continue; }

    for (int i = 0; i < lines.Length - 1; i++)
    {
        var line = lines[i];

        // Line must contain the trigger and BOID
        if (!line.Contains("Update failed, err number is -2039"))
            continue;

        var mBoid = reBoid.Match(line);
        if (!mBoid.Success)
            continue;

        // Next line must confirm (RDR1)(ODBC -2039)
        if (!reRdr1.IsMatch(lines[i + 1]))
            continue;

        int docEntry = int.Parse(mBoid.Groups[1].Value);
        if (docEntries.Add(docEntry))
            Console.WriteLine($"  Found DocEntry {docEntry}  in {Path.GetFileName(file)}:{i + 1}");
    }
}

Console.WriteLine();

if (docEntries.Count == 0)
{
    Console.WriteLine("  No ODBC -2039 Sales Order errors found in logs.");
    Environment.Exit(0);
}

Console.WriteLine($"  Total unique DocEntries found: {docEntries.Count}");
Console.WriteLine("  Looking up DocNum and U_CXS_TRID from SQL ...");
Console.WriteLine();

// --- SQL lookup --------------------------------------------------------------
string idList = string.Join(",", docEntries);
string connStr = $"Server={sqlServer};Database={sqlDb};User Id={sqlUser};Password={sqlPass};TrustServerCertificate=True;";
string query   = $"SELECT DocEntry, DocNum, U_CXS_TRID FROM ORDR WITH(NOLOCK) WHERE DocEntry IN ({idList})";

var results = new List<(int DocEntry, string DocNum, string Trid)>();

using (var conn = new SqlConnection(connStr))
{
    conn.Open();
    using var cmd = new SqlCommand(query, conn);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        results.Add((
            reader.GetInt32(0),
            reader.GetInt32(1).ToString(),
            reader.IsDBNull(2) ? "" : reader.GetString(2)
        ));
    }
}

// Add any DocEntries not found in SQL
foreach (var de in docEntries)
{
    if (!results.Exists(r => r.DocEntry == de))
        results.Add((de, "NOT FOUND", ""));
}

results.Sort((a, b) => a.DocEntry.CompareTo(b.DocEntry));

// --- Export CSV --------------------------------------------------------------
string outPath = Path.Combine(Environment.CurrentDirectory, "ODBC2039_Found.csv");
using (var csv = new StreamWriter(outPath, false, System.Text.Encoding.UTF8))
{
    csv.WriteLine("DocEntry,DocNum,U_CXS_TRID");
    foreach (var r in results)
        csv.WriteLine($"{r.DocEntry},{r.DocNum},{r.Trid}");
}

Console.WriteLine("  Results exported to:");
Console.WriteLine($"  {outPath}");
Console.WriteLine();

// Print table
Console.WriteLine($"  {"DocEntry",-12} {"DocNum",-12} {"U_CXS_TRID"}");
Console.WriteLine($"  {new string('-', 50)}");
foreach (var r in results)
    Console.WriteLine($"  {r.DocEntry,-12} {r.DocNum,-12} {r.Trid}");

Console.WriteLine();
Console.WriteLine("  To fix all found orders, run:");
foreach (var r in results)
{
    if (r.DocNum != "NOT FOUND")
        Console.WriteLine($"    Run-Fix.bat docnum:{r.DocNum}");
}
Console.WriteLine();
