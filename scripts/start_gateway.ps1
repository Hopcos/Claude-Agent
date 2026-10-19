param(
    [int]$Port = 5055
)

$ErrorActionPreference = "Stop"

# 前台实时运行 ClaudeAgent 网关（开发与调试）
Write-Host "Starting ClaudeAgent gateway at http://127.0.0.1:$Port ..." -ForegroundColor Cyan
dotnet run --project (Join-Path $PSScriptRoot "..\src\ClaudeAgent\ClaudeAgent.csproj") --urls "http://127.0.0.1:$Port"
