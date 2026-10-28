<#
.SYNOPSIS
    Extract the Claude Code refresh token (for Docker Method A CLAUDE_REFRESH_TOKEN).
    Multi-source fallback: 1) current credentials file -> 2) gateway state file -> 3) backup.
.DESCRIPTION
    After 'claude auth login', the freshest valid refresh token is in ~/.claude/.credentials.json.
    The local Claude Code CLI occasionally clears that file (see FAQ Q3); when that happens we
    fall back to the gateway's own state file, then the credentials backup.
    Run from Windows PowerShell on the machine where Claude Code is logged in:
        .\scripts\get_refresh_token.ps1
#>
function Get-ClaudeRefreshToken {
  $h = "$env:USERPROFILE\.claude"
  # 1) Current credentials file (freshest after a login)
  $cur = "$h\.credentials.json"
  if (Test-Path $cur) {
    $rt = (Get-Content $cur -Raw | ConvertFrom-Json).claudeAiOauth.refreshToken
    if ($rt) { return @{ Token = $rt; Source = "current credentials file ($cur)" } }
  }
  # 2) Gateway's own state file (fallback when the CLI cleared the credentials file)
  $state = "$h\claude-agent-state.json"
  if (Test-Path $state) {
    $rt = (Get-Content $state -Raw | ConvertFrom-Json).refresh_token
    if ($rt) { return @{ Token = $rt; Source = "gateway state file ($state)" } }
  }
  # 3) Credentials backup (kept automatically on write-back)
  $bak = "$h\.credentials.json.bak"
  if (Test-Path $bak) {
    $rt = (Get-Content $bak -Raw | ConvertFrom-Json).claudeAiOauth.refreshToken
    if ($rt) { return @{ Token = $rt; Source = "credentials backup ($bak)" } }
  }
  return $null
}

$r = Get-ClaudeRefreshToken
if ($r) {
  Write-Host "Source: $($r.Source)"
  Write-Host "refreshToken: $($r.Token)"
} else {
  Write-Host "No valid refreshToken found. Please run 'claude auth login' and try again." -ForegroundColor Red
}
