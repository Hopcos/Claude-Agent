# Test Claude Code OAuth refresh against the real endpoint
param([switch]$NoRefresh)

$c = Get-Content "$env:USERPROFILE\.claude\.credentials.json" -Raw | ConvertFrom-Json
$o = $c.claudeAiOauth

$rt = if ($o.refreshToken -is [string]) { $o.refreshToken } else { $o.refreshToken -join ' ' }
$scopes = if ($o.scopes -is [string]) { $o.scopes } else { $o.scopes -join ' ' }
$clientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"

$payload = @{
    grant_type    = 'refresh_token'
    refresh_token = $rt
    client_id     = $clientId
    scope         = $scopes
} | ConvertTo-Json -Compress

Write-Host "request (masked): $($payload -replace '\"refresh_token\":\"sk-ant-[^\"]+\"', '\"refresh_token\":\"<masked>\"')"

$headers = @{
    'User-Agent'     = 'claude-cli/2.1.301 (external, cli)'
    'anthropic-beta' = 'oauth-2025-04-20'
}

try {
    $r = Invoke-WebRequest -Uri 'https://platform.claude.com/v1/oauth/token' `
        -Method Post -ContentType 'application/json' -Body $payload `
        -Headers $headers -TimeoutSec 30 -ErrorAction Stop

    Write-Host "STATUS: $($r.StatusCode)"
    $resp = $r.Content | ConvertFrom-Json

    # Persist full response for gateway implementation reference
    $resp | ConvertTo-Json -Depth 5 | Out-File "$PSScriptRoot\refresh_response.json" -Encoding utf8

    foreach ($p in $resp.PSObject.Properties) {
        $v = $p.Value
        if ($v -is [string] -and $v.Length -gt 20 -and $p.Name -match 'token') {
            Write-Host "$($p.Name): $($v.Substring(0,14))... (len $($v.Length))"
        }
        else {
            Write-Host "$($p.Name): $v"
        }
    }
}
catch {
    Write-Host "HTTP $($_.Exception.Response.StatusCode)"
    try {
        $stream = $_.Exception.Response.GetResponseStream()
        $reader = New-Object IO.StreamReader($stream)
        $reader.BaseStream.Seek(0, 'Begin') | Out-Null
        Write-Host "BODY: $($reader.ReadToEnd())"
    } catch { Write-Host "no body: $($_.Exception.Message)" }
}
