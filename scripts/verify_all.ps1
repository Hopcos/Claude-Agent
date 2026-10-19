param(
    [string]$BaseUrl = "http://127.0.0.1:5055/v1"
)

$ErrorActionPreference = "Stop"

Write-Host "===========================================================" -ForegroundColor Cyan
Write-Host "   ClaudeAgent .NET 10 Verification Suite (Full Loop)      " -ForegroundColor Green
Write-Host "===========================================================" -ForegroundColor Cyan
Write-Host "Target Base URL: $BaseUrl" -ForegroundColor Gray

$repo = Join-Path $PSScriptRoot ".."

# Step 0: 确保 Claude Code 凭据可用
$credFile = Join-Path $env:USERPROFILE ".claude\.credentials.json"
if (Test-Path $credFile) {
    Write-Host "`n[Step 0/2] Claude Code credentials file found: $credFile" -ForegroundColor Yellow
} else {
    Write-Warning "Claude Code credentials file not found; gateway will fail to refresh tokens."
}

# Step 1: 确保网关在线（未运行则后台拉起）
$healthUrl = "$BaseUrl/health"
$started = $false
try {
    $null = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 3
} catch {
    Write-Host "`n[Step 1/2] Gateway not running; starting in background..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "start_background.ps1") -Port 5055
    $started = $true
}

# Step 2: 运行 .NET 10 xUnit 测试套件
Write-Host "`n[Step 2/2] Running .NET 10 Test Project (ClaudeAgent.Tests)..." -ForegroundColor Yellow
$testProj = (Resolve-Path (Join-Path $repo "tests\ClaudeAgent.Tests\ClaudeAgent.Tests.csproj")).Path

Write-Host "Compiling test suite with .NET 10 SDK..." -ForegroundColor Gray
dotnet build "$testProj"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test project build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "Executing xUnit test runner..." -ForegroundColor Gray
dotnet test "$testProj" --no-build --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test execution failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

if ($started) {
    Write-Host "`n(Leaving gateway running in background; stop with scripts\stop_gateway.ps1)" -ForegroundColor DarkGray
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host "   ALL .NET 10 TESTS PASSED SUCCESSFULLY! (100% PASS)      " -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
