# ==============================================================================
#  Schedule-Detect.ps1
#
#  Registers a Windows Scheduled Task that runs Detect-ODBC2039.csx daily,
#  auto-fixing at-risk Sales Orders before business hours.
#
#  Run ONCE as Administrator on the machine that will act as the scheduler
#  (any machine with SQL access + SAP DI API installed).
#
#  USAGE:
#    powershell -ExecutionPolicy Bypass -File Schedule-Detect.ps1
#    powershell -ExecutionPolicy Bypass -File Schedule-Detect.ps1 -Time 05:30
#    powershell -ExecutionPolicy Bypass -File Schedule-Detect.ps1 -DryRun
#    powershell -ExecutionPolicy Bypass -File Schedule-Detect.ps1 -Remove
#    powershell -ExecutionPolicy Bypass -File Schedule-Detect.ps1 -Force
# ==============================================================================

param(
    [string] $Time   = "06:00",   # Daily run time (HH:mm, 24-hour)
    [switch] $Remove,             # Unregister the task
    [switch] $DryRun,             # Register in report-only mode (no DI API fix)
    [switch] $Force               # Overwrite an existing task without prompting
)

$ErrorActionPreference = "Stop"

$TaskName    = "SAP-ODBC2039-AutoFix"
$ScriptDir   = $PSScriptRoot
$DetectCsx   = Join-Path $ScriptDir "Detect-ODBC2039.csx"
$LogDir      = Join-Path $ScriptDir "Logs"
$LogFile     = Join-Path $LogDir   "detect.log"
$RunnerBat   = Join-Path $ScriptDir "_detect-runner.bat"

function Write-Ok($msg)   { Write-Host "  [OK]  $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  [!!]  $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "  [..]  $msg" -ForegroundColor Cyan }

Write-Host ""
Write-Host "  SAP ODBC-2039 Auto-Fix — Scheduled Task Setup" -ForegroundColor Cyan
Write-Host "  ================================================" -ForegroundColor Cyan
Write-Host ""

# ── Remove mode ───────────────────────────────────────────────────────────────
if ($Remove)
{
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
    {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Ok "Scheduled task '$TaskName' removed."
        if (Test-Path $RunnerBat) { Remove-Item $RunnerBat -Force }
        Write-Ok "Runner batch file removed."
    }
    else
    {
        Write-Info "Task '$TaskName' not found — nothing to remove."
    }
    Write-Host ""
    exit 0
}

# ── Validate prerequisites ────────────────────────────────────────────────────
Write-Info "Checking Detect-ODBC2039.csx ..."
if (!(Test-Path $DetectCsx))
{
    Write-Fail "Detect-ODBC2039.csx not found at: $DetectCsx"
    exit 1
}
Write-Ok "Detect-ODBC2039.csx found."

Write-Info "Checking config.json ..."
if (!(Test-Path (Join-Path $ScriptDir "config.json")))
{
    Write-Fail "config.json not found. Fill in credentials before scheduling."
    exit 1
}
Write-Ok "config.json found."

Write-Info "Locating dotnet-script ..."
$DotnetScriptExe = $null

# Try PATH first (works if dotnet-script is already in the session)
$dsCmd = Get-Command dotnet-script -ErrorAction SilentlyContinue
if ($dsCmd) { $DotnetScriptExe = $dsCmd.Source }

# Fall back to the default global tool install path
if (!$DotnetScriptExe -or !(Test-Path $DotnetScriptExe))
{
    $DotnetScriptExe = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-script.exe"
}

if (!(Test-Path $DotnetScriptExe))
{
    Write-Fail "dotnet-script.exe not found at: $DotnetScriptExe"
    Write-Host "  Run Setup.ps1 first to install dotnet-script." -ForegroundColor Yellow
    exit 1
}
Write-Ok "dotnet-script: $DotnetScriptExe"

# ── Create Logs directory ─────────────────────────────────────────────────────
if (!(Test-Path $LogDir))
{
    New-Item -ItemType Directory -Path $LogDir | Out-Null
    Write-Ok "Created Logs directory: $LogDir"
}
else
{
    Write-Ok "Logs directory: $LogDir"
}

# ── Generate the runner batch file ───────────────────────────────────────────
#  A batch file is used so cmd.exe handles stdout/stderr redirection reliably.
#  The runner is regenerated each time Schedule-Detect.ps1 runs.
$fixArg = if ($DryRun) { "dryrun" } else { "autofix" }

$batLines = @(
    "@echo off",
    "echo.",
    "echo ======================================== >> `"$LogFile`" 2>&1",
    "echo Run: %DATE% %TIME% >> `"$LogFile`" 2>&1",
    "`"$DotnetScriptExe`" `"$DetectCsx`" -- $fixArg >> `"$LogFile`" 2>&1"
)
[System.IO.File]::WriteAllLines($RunnerBat, $batLines, [System.Text.Encoding]::ASCII)
Write-Ok "Runner batch: $RunnerBat  (mode: $fixArg)"

# ── Check if task already exists ─────────────────────────────────────────────
if ((Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) -and !$Force)
{
    Write-Host ""
    Write-Info "Task '$TaskName' already exists. Use -Force to overwrite."
    Write-Host ""
    exit 0
}

# ── Build task components ─────────────────────────────────────────────────────
$action = New-ScheduledTaskAction `
    -Execute        "cmd.exe" `
    -Argument       "/c `"$RunnerBat`"" `
    -WorkingDirectory $ScriptDir

$trigger = New-ScheduledTaskTrigger -Daily -At $Time

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit       (New-TimeSpan -Minutes 30) `
    -StartWhenAvailable       $true `
    -RunOnlyIfNetworkAvailable $true `
    -MultipleInstances        IgnoreNew

# Run as the current user (S4U = no stored password, user profile loaded)
$currentUser = "$env:USERDOMAIN\$env:USERNAME"
$principal   = New-ScheduledTaskPrincipal `
    -UserId    $currentUser `
    -LogonType S4U `
    -RunLevel  Highest

# ── Register the task ─────────────────────────────────────────────────────────
try
{
    Register-ScheduledTask `
        -TaskName   $TaskName `
        -Action     $action `
        -Trigger    $trigger `
        -Settings   $settings `
        -Principal  $principal `
        -Description "Proactively detects and auto-fixes SAP B1 Sales Orders prone to ODBC -2039 errors. Managed by Schedule-Detect.ps1." `
        -Force:$Force | Out-Null

    Write-Host ""
    Write-Ok "Task '$TaskName' registered successfully."
    Write-Ok "Runs daily at $Time as $currentUser."
    Write-Ok "Log file: $LogFile"
    Write-Host ""
    Write-Host "  Useful commands:" -ForegroundColor Cyan
    Write-Host "    Start-ScheduledTask -TaskName '$TaskName'   <- run now" -ForegroundColor Cyan
    Write-Host "    Get-ScheduledTask   -TaskName '$TaskName'   <- check status" -ForegroundColor Cyan
    Write-Host "    powershell -File Schedule-Detect.ps1 -Remove  <- unregister" -ForegroundColor Cyan
}
catch
{
    Write-Fail "Failed to register task: $_"
    Write-Host "  Re-run as Administrator." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
