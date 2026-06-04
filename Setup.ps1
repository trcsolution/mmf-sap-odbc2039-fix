# ==============================================================================
#  Setup.ps1
#  Checks prerequisites and installs dotnet-script for Fix_SAP_Order_ODBC2039
# ==============================================================================

$ErrorActionPreference = "Stop"

function Write-Ok($msg)   { Write-Host "  [OK]  $msg" -ForegroundColor Green }
function Write-Fail($msg) { Write-Host "  [!!]  $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "  [..]  $msg" -ForegroundColor Cyan }

Write-Host ""
Write-Host "  SAP B1 ODBC-2039 Fix — Prerequisites Setup" -ForegroundColor Cyan
Write-Host "  ============================================" -ForegroundColor Cyan
Write-Host ""

# ------------------------------------------------------------------------------
# 1. Check .NET SDK
# ------------------------------------------------------------------------------
Write-Info "Checking .NET SDK ..."
try {
    $dotnetVer = dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) { throw }
    $major = [int]($dotnetVer -split '\.')[0]
    if ($major -lt 6) {
        Write-Fail ".NET SDK $dotnetVer found but version 6 or higher is required."
        Write-Host ""
        Write-Host "  Download: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
        exit 1
    }
    Write-Ok ".NET SDK $dotnetVer"
} catch {
    Write-Fail ".NET SDK not found."
    Write-Host ""
    Write-Host "  Download: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    exit 1
}

# ------------------------------------------------------------------------------
# 2. Check / install dotnet-script
# ------------------------------------------------------------------------------
Write-Info "Checking dotnet-script ..."
$scriptVer = dotnet script --version 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Ok "dotnet-script $scriptVer already installed."
} else {
    Write-Info "dotnet-script not found. Installing ..."
    dotnet tool install -g dotnet-script
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Failed to install dotnet-script."
        exit 1
    }
    # Refresh PATH for this session
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("PATH","User")
    $scriptVer = dotnet script --version 2>&1
    Write-Ok "dotnet-script $scriptVer installed successfully."
}

# ------------------------------------------------------------------------------
# 3. Check SAP DI API COM registration
# ------------------------------------------------------------------------------
Write-Info "Checking SAP DI API COM registration ..."
$key = "HKLM:\SOFTWARE\Classes\SAPbobsCOM.Company"
if (Test-Path $key) {
    Write-Ok "SAPbobsCOM.Company COM class is registered."
} else {
    Write-Fail "SAPbobsCOM.Company is NOT registered."
    Write-Host ""
    Write-Host "  Install the SAP B1 DI API component from the SAP installation media." -ForegroundColor Yellow
    Write-Host "  Expected path after install: C:\Program Files\SAP\SAP Business One DI API\" -ForegroundColor Yellow
}

# ------------------------------------------------------------------------------
# 4. Check config.json exists
# ------------------------------------------------------------------------------
Write-Info "Checking config.json ..."
$configPath = Join-Path $PSScriptRoot "config.json"
if (Test-Path $configPath) {
    Write-Ok "config.json found."
} else {
    Write-Fail "config.json not found at: $configPath"
    Write-Host "  Create it from config.json in this folder and fill in your connection details." -ForegroundColor Yellow
}

# ------------------------------------------------------------------------------
Write-Host ""
Write-Host "  Setup complete." -ForegroundColor Cyan
Write-Host ""
