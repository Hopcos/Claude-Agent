param(
    [int]$Port = 5055
)

# 后台静默守护运行（日志写入 logs/）
$ErrorActionPreference = "Stop"

$repo = Join-Path $PSScriptRoot ".."
$logsDir = Join-Path $repo "logs"
if (!(Test-Path $logsDir)) { New-Item -ItemType Directory -Path $logsDir | Out-Null }

$proj = Join-Path $repo "src\ClaudeAgent\ClaudeAgent.csproj"

Write-Host "Building gateway..." -ForegroundColor Gray
dotnet build $proj | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Write-Host "Starting ClaudeAgent gateway in background at http://127.0.0.1:$Port ..." -ForegroundColor Cyan
$stdout = Join-Path $logsDir "gateway_stdout.log"
$stderr = Join-Path $logsDir "gateway_stderr.log"

Start-Process -FilePath "dotnet" `
    -ArgumentList "run", "--project", $proj, "--no-build", "--no-launch-profile", "--urls", "http://127.0.0.1:$Port" `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden

Start-Sleep -Seconds 4

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/v1/health" -Method Get -TimeoutSec 5
    Write-Host "Gateway is healthy:" -ForegroundColor Green
    Write-Host "  status:      $($health.status)"
    Write-Host "  account:     $($health.account.account)"
    Write-Host "  token:       $($health.account.hasToken), expires $($health.account.expiresUtc)"
    Write-Host "  default:     $($health.defaultModel)"
    Write-Host "  models:      $($health.allModels.Count)"
} catch {
    Write-Warning "Gateway did not answer health check yet; see logs\gateway_stderr.log"
}
