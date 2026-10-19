# 自驱 OAuth 流程 v3：PKCE 生成 -> 授权 URL -> 等待 code -> 带重试的交换 -> 成功后直接写回凭据文件
$ErrorActionPreference = "Stop"
$proxy = "http://10.0.5.230:7890"
$env:HTTPS_PROXY = $proxy
$env:HTTP_PROXY  = $proxy

function RandUrl([int] $len) {
    $chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
    $bytes = New-Object byte[] $len
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $rng.GetBytes($bytes)
    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) { $null = $sb.Append($chars[$b % 64]) }
    $sb.ToString()
}

$verifier = RandUrl 43
$sha = New-Object System.Security.Cryptography.SHA256Managed
$challenge = [Convert]::ToBase64String($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($verifier))).TrimEnd('=').Replace('+','-').Replace('/','_')

$clientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"
$state = RandUrl 43
$redirectUri = "https://platform.claude.com/oauth/code/callback"
$scope = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload user:plugins"

$authUrl = "https://claude.com/cai/oauth/authorize?code=true&client_id=$clientId&response_type=code" +
    "&redirect_uri=" + [uri]::EscapeDataString($redirectUri) +
    "&scope=" + [uri]::EscapeDataString($scope) +
    "&code_challenge=$challenge&code_challenge_method=S256&state=$state&login_method=sso"

Write-Host "AUTH_URL_BEGIN"
Write-Host $authUrl
Write-Host "AUTH_URL_END"

function Post-Token([string] $jsonBody, [string] $tag) {
    for ($attempt = 1; $attempt -le 100; $attempt++) {
        try {
            $resp = Invoke-WebRequest -Uri "https://platform.claude.com/v1/oauth/token" `
                -Method Post -ContentType "application/json" -Body $jsonBody `
                -TimeoutSec 30 -ErrorAction Stop
            Write-Host "[$tag] HTTP $($resp.StatusCode) (attempt $attempt)"
            return @{ status = [int]$resp.StatusCode; body = $resp.Content }
        } catch {
            $sc = $_.Exception.Response
            $code = if ($sc) { [int]$sc.StatusCode } else { 0 }
            $body = ""
            try { if ($sc) { $sr = New-Object System.IO.StreamReader($sc.GetResponseStream()); $body = $sr.ReadToEnd() } } catch {}
            if ($code -eq 429) {
                Write-Host "[$tag] 429 rate-limited (attempt $attempt) - retry in 6s"
                Start-Sleep -Seconds 6
                continue
            }
            Write-Host "[$tag] HTTP $code BODY> $body (attempt $attempt)"
            return @{ status = $code; body = $body }
        }
    }
    return @{ status = 0; body = "retry exhausted" }
}

# 等待授权码（30 分钟）
$codeFile = "D:\bs\code\others\Claude-Agent\investigation\login_code.txt"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$code = $null
while ($sw.Elapsed.TotalMinutes -lt 30) {
    if (Test-Path $codeFile) { $code = (Get-Content $codeFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 500
}
if (-not $code) { Write-Host "TIMEOUT WAITING FOR CODE"; exit 1 }

$codePart = ($code -split '#')[0]
$statePart = if ($code -match '#(.+)') { $Matches[1] } else { $null }
Write-Host "EXCHANGING (code=$($codePart.Substring(0,10))..., state-match=$($statePart -eq $state))"

$bodyJson = @{
    grant_type    = "authorization_code"
    code          = $codePart
    redirect_uri  = $redirectUri
    client_id     = $clientId
    code_verifier = $verifier
} | ConvertTo-Json

$r = Post-Token $bodyJson "REAL"
Write-Host "FINAL STATUS: $($r.status)"
Write-Host "FINAL BODY> $($r.body)"

if ($r.status -ne 200) {
    $r.body | Out-File "D:\bs\code\others\Claude-Agent\investigation\exchange_result.json" -Encoding utf8
    Write-Host "EXCHANGE FAILED"
    exit 1
}

# 成功：解析并直接写回凭据文件
$tok = $r.body | ConvertFrom-Json
$nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$accessExp = $nowMs + [int64]($tok.expires_in * 1000) - 30000
$refreshExp = if ($tok.refresh_token_expires_in) { $nowMs + [int64]($tok.refresh_token_expires_in * 1000) } else { $nowMs + [int64](30*86400*1000) }

$credPath = "$env:USERPROFILE\.claude\.credentials.json"
$backup = "D:\bs\code\others\Claude-Agent\investigation\credentials.before_gateway_write.json"
Copy-Item $credPath $backup -Force

$newCred = @{
    claudeAiOauth = [ordered]@{
        accessToken            = $tok.access_token
        refreshToken           = $tok.refresh_token
        expiresAt              = $accessExp
        refreshTokenExpiresAt  = $refreshExp
        scopes                 = @($tok.scope -split ' ' | Where-Object { $_ })
        subscriptionType       = "enterprise"
        rateLimitTier          = "default_claude_zero"
    }
}
$json = $newCred | ConvertTo-Json -Depth 5
$tmp = "$credPath.tmp"
$json | Out-File $tmp -Encoding utf8
Move-Item $tmp $credPath -Force

Write-Host "CREDENTIALS WRITTEN OK (masked):"
$masked = $json -replace '"(accessToken|refreshToken)"\s*:\s*"[^"]+"', '"$1":"<MASKED>"'
Write-Host $masked
Write-Host "EXCHANGE SUCCESS"
