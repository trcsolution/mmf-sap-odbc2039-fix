#!/usr/bin/env dotnet-script
//==============================================================================
//  Fix_SAP_Order_ODBC2039.csx
//  Opens a SAP B1 Sales Order or Purchase Order via DI API and calls Update()
//  to re-commit inventory and clear the ODBC -2039 lock.
//
//  USAGE (Sales Orders):
//    dotnet script Fix_SAP_Order_ODBC2039.csx                          <- default DocNum (SO)
//    dotnet script Fix_SAP_Order_ODBC2039.csx -- docnum:5054686        <- Sales Order by DocNum
//    dotnet script Fix_SAP_Order_ODBC2039.csx -- trid:TXN-00123        <- Sales Order by U_CXS_TRID
//
//  USAGE (Purchase Orders):
//    dotnet script Fix_SAP_Order_ODBC2039.csx -- po-docnum:5054686     <- Purchase Order by DocNum
//    dotnet script Fix_SAP_Order_ODBC2039.csx -- po-trid:TXN-00123     <- Purchase Order by U_CXS_TRID
//==============================================================================

#r "nuget: Microsoft.Data.SqlClient, 5.2.1"

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// ---------- LOAD config.json ------------------------------------------------
var configPath = Path.Combine(Environment.CurrentDirectory, "config.json");
if (!File.Exists(configPath))
{
    Console.WriteLine($"ERROR: config.json not found at {configPath}");
    Console.WriteLine("       Copy config.json to the script folder and fill in your connection details.");
    Environment.Exit(1);
}

var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var cfg = configDoc.RootElement;

string SqlServer     = cfg.GetProperty("SqlServer").GetString()!;
string CompanyDB     = cfg.GetProperty("CompanyDB").GetString()!;
string UserName      = cfg.GetProperty("UserName").GetString()!;
string Password      = cfg.GetProperty("Password").GetString()!;
string DbUserName    = cfg.GetProperty("DbUserName").GetString()!;
string DbPassword    = cfg.GetProperty("DbPassword").GetString()!;
int    DefaultDocNum = cfg.GetProperty("DefaultDocNum").GetInt32();
int    DbSrvType     = cfg.GetProperty("DbServerType").GetInt32();
string LicenseServer = cfg.GetProperty("LicenseServer").GetString()!;
string SLDServer     = cfg.GetProperty("SLDServer").GetString()!;
// ----------------------------------------------------------------------------

// --- Parse argument: po-docnum:XXXXXX  po-trid:XXXXXX  docnum:XXXXXX  trid:XXXXXX  or plain number --------
string lookupMode  = "docnum";
string lookupValue = DefaultDocNum.ToString();
string orderType   = "SO";   // SO = Sales Order, PO = Purchase Order

if (Args.Count > 0)
{
    var arg = Args[0].Trim();
    if (arg.StartsWith("po-trid:", StringComparison.OrdinalIgnoreCase))
    {
        orderType   = "PO";
        lookupMode  = "trid";
        lookupValue = arg.Substring(8).Trim();
    }
    else if (arg.StartsWith("po-docnum:", StringComparison.OrdinalIgnoreCase))
    {
        orderType   = "PO";
        lookupMode  = "docnum";
        lookupValue = arg.Substring(10).Trim();
    }
    else if (arg.StartsWith("trid:", StringComparison.OrdinalIgnoreCase))
    {
        orderType   = "SO";
        lookupMode  = "trid";
        lookupValue = arg.Substring(5).Trim();
    }
    else if (arg.StartsWith("docnum:", StringComparison.OrdinalIgnoreCase))
    {
        orderType   = "SO";
        lookupMode  = "docnum";
        lookupValue = arg.Substring(7).Trim();
    }
    else
    {
        // plain number treated as Sales Order DocNum
        orderType   = "SO";
        lookupMode  = "docnum";
        lookupValue = arg;
    }
}

// Derive table name, DI API business object type and display label from orderType
string tableName  = orderType == "PO" ? "OPOR" : "ORDR";
int    boType     = orderType == "PO" ? 22 : 17;   // 22 = boPurchaseOrders, 17 = boOrders
string orderLabel = orderType == "PO" ? "Purchase Order" : "Sales Order";

Console.WriteLine($"Order type  : {orderLabel}");
Console.WriteLine($"Lookup mode : {lookupMode.ToUpper()}");
Console.WriteLine($"Lookup value: {lookupValue}");

// --- Resolve to DocEntry via SQL --------------------------------------------
int docEntry = 0;
string connStr = $"Server={SqlServer};Database={CompanyDB};User Id={DbUserName};Password={DbPassword};TrustServerCertificate=True;";

using (var sql = new SqlConnection(connStr))
{
    sql.Open();

    string query = lookupMode == "trid"
        ? $"SELECT DocEntry FROM {tableName} WITH(NOLOCK) WHERE U_CXS_TRID = @val"
        : $"SELECT DocEntry FROM {tableName} WITH(NOLOCK) WHERE DocNum = @val";

    using var cmd = new SqlCommand(query, sql);
    cmd.Parameters.AddWithValue("@val", lookupValue);
    var result = cmd.ExecuteScalar();

    if (result == null)
    {
        Console.WriteLine($"ERROR: No {orderLabel} found where {lookupMode.ToUpper()} = '{lookupValue}'.");
        Environment.Exit(1);
    }
    docEntry = Convert.ToInt32(result);
}

Console.WriteLine($"Resolved to DocEntry {docEntry}");
Console.WriteLine($"Connecting to {CompanyDB} on {SqlServer} ...");

// --- Connect via DI API -----------------------------------------------------
dynamic company = Activator.CreateInstance(Type.GetTypeFromProgID("SAPbobsCOM.Company"))
    ?? throw new Exception("Could not create SAPbobsCOM.Company COM object. Is the DI API installed?");

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

int ret = company.Connect();
if (ret != 0)
{
    Console.WriteLine($"CONNECTION FAILED.  Code={company.GetLastErrorCode()}  {company.GetLastErrorDescription()}");
    Environment.Exit(1);
}
Console.WriteLine("Connected OK.");

// --- Retrieve the Sales Order or Purchase Order -----------------------------
dynamic order = company.GetBusinessObject(boType); // 17 = boOrders, 22 = boPurchaseOrders

if (!(bool)order.GetByKey(docEntry))
{
    Console.WriteLine($"GetByKey({docEntry}) FAILED.  Code={company.GetLastErrorCode()}  {company.GetLastErrorDescription()}");
    company.Disconnect();
    Environment.Exit(1);
}

Console.WriteLine($"{orderLabel} retrieved: DocEntry={docEntry}  DocNum={order.DocNum}  Status={order.DocumentStatus}");

// --- Must be open (DocumentStatus = 0 = bost_Open) --------------------------
if ((int)order.DocumentStatus != 0)
{
    Console.WriteLine($"{orderLabel} is already closed (status={order.DocumentStatus}). Nothing to do.");
    company.Disconnect();
    Environment.Exit(0);
}

// --- Set Comments field (toggle trailing '.') --------------------------------
string currentComments = (string)order.Comments ?? "";
if (string.IsNullOrEmpty(currentComments))
    order.Comments = ".";
else if (currentComments.EndsWith("."))
    order.Comments = currentComments.TrimEnd('.');
else
    order.Comments = currentComments + ".";

// --- Update -----------------------------------------------------------------
Console.WriteLine("Calling Update() ...");
ret = order.Update();

if (ret != 0)
{
    int errCode = company.GetLastErrorCode();
    string errMsg = company.GetLastErrorDescription();
    Console.WriteLine($"UPDATE FAILED.  Code={errCode}  {errMsg}");
    if (errCode == -2039)
    {
        Console.WriteLine();
        Console.WriteLine(">>> Still -2039: check for blocking sessions in SSMS and retry.");
    }
    company.Disconnect();
    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"SUCCESS!  {orderLabel} {docEntry} (DocNum {order.DocNum}) updated via DI API.");
Console.WriteLine("SAP has re-evaluated the order. The ODBC -2039 lock should now be cleared.");
Console.WriteLine();
Console.WriteLine($"  *** IMPORTANT: Do NOT open the {orderLabel} in SAP yet! ***");
Console.WriteLine("  Waiting for SAP connection to disconnect cleanly ...");

company.Disconnect();

Console.WriteLine($"  SAP connection closed. You may now open the {orderLabel} in SAP B1.");
