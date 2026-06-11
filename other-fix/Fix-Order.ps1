<#
.SYNOPSIS
    Fix SAP B1 sales orders that throw (RDR1) -2039 when edited in the UI.

.DESCRIPTION
    Root cause: Orders created by an external integration (DataSource='O') whose RDR1
    rows have never been written by the DI API. The DI API uses ODBC CONCUR_ROWVER
    cursors; if a row's rowversion has never been refreshed by DI API, its own Update()
    process triggers a version conflict against itself -> -2039.

    Fix: PATCH the order via Service Layer including all DocumentLines.
    This forces an UPDATE on every RDR1 row, refreshing their rowversions so the
    DI API can update the document normally afterwards.

.EXAMPLE
    # Fix one order by the number you see in the UI:
    .\Fix-Order.ps1 -DocNum 1181212

.EXAMPLE
    # Fix one order by internal DocEntry:
    .\Fix-Order.ps1 -DocEntry 444633

.EXAMPLE
    # Auto-detect and fix ALL currently broken orders:
    .\Fix-Order.ps1 -All

.EXAMPLE
    # Preview what -All would do without making any changes:
    .\Fix-Order.ps1 -All -DryRun
#>

[CmdletBinding(DefaultParameterSetName = 'All')]
param(
    [Parameter(ParameterSetName = 'ByDocNum',   Mandatory)]
    [int]$DocNum,

    [Parameter(ParameterSetName = 'ByDocEntry', Mandatory)]
    [int]$DocEntry,

    [Parameter(ParameterSetName = 'All',        Mandatory)]
    [switch]$All,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Configuration ──────────────────────────────────────────────────────────────
$SL_BASE  = 'https://rs-dell:50000/b1s/v1'
$COMPANY  = 'Z_SAP_MURPHY_140526'
$SL_USER  = 'manager'
$SL_PASS  = 'MMSap2023!'

$SQL_CONN = 'Server=rs-dell;Database=Z_SAP_MURPHY_140526;User Id=sa;Password=B1Admin!;TrustServerCertificate=True;'

# ── SQL helpers ─────────────────────────────────────────────────────────────────
function New-SqlConnection {
    $conn = New-Object System.Data.SqlClient.SqlConnection($SQL_CONN)
    $conn.Open()
    return $conn
}

function Invoke-SqlQuery {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Query,
        [hashtable]$Params = @{}
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Query
    foreach ($kv in $Params.GetEnumerator()) {
        $cmd.Parameters.AddWithValue($kv.Key, $kv.Value) | Out-Null
    }
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
    $table   = New-Object System.Data.DataTable
    $adapter.Fill($table) | Out-Null
    return $table
}

function Get-BrokenOrders {
    param([System.Data.SqlClient.SqlConnection]$Connection)

    $sql = @"
        SELECT DISTINCT o.DocEntry, o.DocNum
        FROM ORDR o
        JOIN RDR1 r  ON r.DocEntry = o.DocEntry
        JOIN OITM i  ON i.ItemCode = r.ItemCode
        WHERE o.DocStatus  = 'O'
          AND o.Canceled   = 'N'
          AND o.DataSource = 'O'
          AND o.DataVers   = 1
          AND i.ManSerNum  = 'Y'
          AND NOT EXISTS (
              SELECT 1 FROM ADOC a
              WHERE a.DocEntry = o.DocEntry
                AND a.ObjType  = '17'
          )
        ORDER BY o.DocEntry
"@
    return Invoke-SqlQuery -Connection $Connection -Query $sql
}

function Get-DocEntryFromDocNum {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [int]$DocNum
    )
    $table = Invoke-SqlQuery -Connection $Connection `
        -Query 'SELECT DocEntry FROM ORDR WHERE DocNum = @dn' `
        -Params @{ '@dn' = $DocNum }

    if ($table.Rows.Count -eq 0) {
        Write-Error "DocNum $DocNum not found in ORDR."
    }
    return [int]$table.Rows[0]['DocEntry']
}

function Get-OrderInfo {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [int]$DocEntry
    )

    $header = Invoke-SqlQuery -Connection $Connection `
        -Query 'SELECT DocNum, Comments FROM ORDR WHERE DocEntry = @de' `
        -Params @{ '@de' = $DocEntry }

    if ($header.Rows.Count -eq 0) {
        Write-Error "DocEntry $DocEntry not found in ORDR."
    }

    $lines = Invoke-SqlQuery -Connection $Connection `
        -Query 'SELECT LineNum, FreeTxt FROM RDR1 WHERE DocEntry = @de ORDER BY LineNum' `
        -Params @{ '@de' = $DocEntry }

    return @{
        DocNum   = [int]$header.Rows[0]['DocNum']
        Comments = if ($header.Rows[0]['Comments'] -is [DBNull]) { '' } else { [string]$header.Rows[0]['Comments'] }
        Lines    = $lines
    }
}

# ── Service Layer helpers ───────────────────────────────────────────────────────
function Connect-ServiceLayer {
    $body = @{
        CompanyDB = $COMPANY
        UserName  = $SL_USER
        Password  = $SL_PASS
    } | ConvertTo-Json

    $response = Invoke-RestMethod `
        -Uri             "$SL_BASE/Login" `
        -Method          Post `
        -Body            $body `
        -ContentType     'application/json' `
        -SessionVariable 'webSession' `
        -SkipCertificateCheck

    # Return the session so callers can pass it to subsequent requests
    return $webSession
}

function Disconnect-ServiceLayer {
    param($WebSession)
    try {
        Invoke-RestMethod `
            -Uri                  "$SL_BASE/Logout" `
            -Method               Post `
            -WebSession           $WebSession `
            -SkipCertificateCheck | Out-Null
    } catch {
        # Ignore logout errors
    }
}

function Invoke-PatchOrder {
    param(
        $WebSession,
        [int]$DocEntry,
        [string]$Comments,
        [System.Data.DataTable]$Lines
    )

    $docLines = @(
        foreach ($row in $Lines.Rows) {
            $ft = if ($row['FreeTxt'] -is [DBNull]) { '' } else { [string]$row['FreeTxt'] }
            @{
                LineNum  = [int]$row['LineNum']
                FreeText = $ft
            }
        }
    )

    $payload = @{
        Comments      = $Comments
        DocumentLines = $docLines
    } | ConvertTo-Json -Depth 5

    try {
        $response = Invoke-RestMethod `
            -Uri                  "$SL_BASE/Orders($DocEntry)" `
            -Method               Patch `
            -Body                 $payload `
            -ContentType          'application/json' `
            -WebSession           $WebSession `
            -SkipCertificateCheck `
            -StatusCodeVariable   'statusCode'

        return @{ OK = $true;  Status = $statusCode; Message = "HTTP $statusCode" }
    } catch {
        $statusCode = [int]$_.Exception.Response.StatusCode
        $body       = $_.ErrorDetails.Message
        if (-not $body) { $body = $_.Exception.Message }
        return @{ OK = $false; Status = $statusCode; Message = "HTTP $statusCode  $($body.Substring(0, [Math]::Min(200, $body.Length)))" }
    }
}

# ── Core fix logic ──────────────────────────────────────────────────────────────
function Invoke-FixOrders {
    param(
        [int[]]$DocEntries,
        [bool]$DryRun
    )

    $conn = New-SqlConnection

    $webSession = $null
    if (-not $DryRun) {
        Write-Host 'Logging in to Service Layer...'
        $webSession = Connect-ServiceLayer
        Write-Host "Logged in.`n"
    } else {
        Write-Host "[DRY RUN — no changes will be made]`n"
    }

    $results = [System.Collections.Generic.List[hashtable]]::new()

    foreach ($de in $DocEntries) {
        $info = Get-OrderInfo -Connection $conn -DocEntry $de
        Write-Host ("DocEntry={0}  DocNum={1}  Lines={2}" -f $de, $info.DocNum, $info.Lines.Rows.Count) -NoNewline

        if ($info.Lines.Rows.Count -eq 0) {
            $msg = 'SKIPPED — no RDR1 lines found'
            Write-Host "  -> $msg"
            $results.Add(@{ DocEntry = $de; DocNum = $info.DocNum; OK = $false; Message = $msg })
            continue
        }

        if ($DryRun) {
            $msg = 'DRY RUN — would patch'
            Write-Host "  -> $msg"
            $results.Add(@{ DocEntry = $de; DocNum = $info.DocNum; OK = $true; Message = $msg })
            continue
        }

        $result = Invoke-PatchOrder -WebSession $webSession -DocEntry $de -Comments $info.Comments -Lines $info.Lines
        Write-Host "  -> $($result.Message)"
        $results.Add(@{ DocEntry = $de; DocNum = $info.DocNum; OK = $result.OK; Message = $result.Message })
    }

    if ($webSession -and -not $DryRun) {
        Disconnect-ServiceLayer -WebSession $webSession
    }

    $conn.Close()
    return $results
}

function Write-Summary {
    param(
        [System.Collections.Generic.List[hashtable]]$Results,
        [bool]$DryRun
    )

    $label   = if ($DryRun) { ' (DRY RUN)' } else { '' }
    $okList  = $Results | Where-Object { $_.OK }
    $badList = $Results | Where-Object { -not $_.OK }

    Write-Host ''
    Write-Host ('=' * 60)
    Write-Host "SUMMARY$label"
    Write-Host ('=' * 60)
    Write-Host "Fixed  : $($okList.Count)"
    foreach ($r in $okList)  { Write-Host "  DocEntry=$($r.DocEntry)  DocNum=$($r.DocNum)" }
    Write-Host "Failed : $($badList.Count)"
    foreach ($r in $badList) { Write-Host "  DocEntry=$($r.DocEntry)  DocNum=$($r.DocNum)  -> $($r.Message)" }

    if ($okList.Count -gt 0 -and -not $DryRun) {
        Write-Host ''
        Write-Host 'Next step: open the fixed orders in the SAP B1 UI and confirm'
        Write-Host 'the Remarks field can now be saved without -2039.'
    }
}

# ── Entry point ─────────────────────────────────────────────────────────────────
switch ($PSCmdlet.ParameterSetName) {

    'All' {
        Write-Host 'Scanning for broken orders...'
        $conn   = New-SqlConnection
        $broken = Get-BrokenOrders -Connection $conn
        $conn.Close()

        if ($broken.Rows.Count -eq 0) {
            Write-Host 'No broken orders found. Nothing to do.'
            exit 0
        }

        Write-Host "Found $($broken.Rows.Count) broken order(s):"
        foreach ($row in $broken.Rows) {
            Write-Host "  DocEntry=$($row['DocEntry'])  DocNum=$($row['DocNum'])"
        }
        Write-Host ''

        $docEntries = @($broken.Rows | ForEach-Object { [int]$_['DocEntry'] })
    }

    'ByDocNum' {
        $conn       = New-SqlConnection
        $de         = Get-DocEntryFromDocNum -Connection $conn -DocNum $DocNum
        $conn.Close()
        $docEntries = @($de)
    }

    'ByDocEntry' {
        $docEntries = @($DocEntry)
    }
}

$results = Invoke-FixOrders -DocEntries $docEntries -DryRun $DryRun.IsPresent
Write-Summary -Results $results -DryRun $DryRun.IsPresent
