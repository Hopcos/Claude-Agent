# 受控登录驱动器 v2：全流捕获 (stdout+stderr -> login_debug.txt)，URL 双流匹配，code 文件注入 stdin
$exe = "$env:USERPROFILE\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"
$codeFile = "D:\bs\code\others\Claude-Agent\investigation\login_code.txt"
$debugFile = "D:\bs\code\others\Claude-Agent\investigation\login_debug.txt"
Remove-Item $codeFile, $debugFile -ErrorAction SilentlyContinue

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = "--debug auth login --sso"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
$psi.EnvironmentVariables["DEBUG"] = "1"
$psi.EnvironmentVariables["ANTHROPIC_LOG"] = "debug"
$p = [System.Diagnostics.Process]::Start($psi)

$debug = New-Object System.Text.StringBuilder
function Log([string] $tag, [string] $line) {
    $script:debug.AppendLine("[$tag] $line") | Out-Null
    Write-Host "$tag> $line"
}

# 双流读取：stdout 逐行阻塞读；stderr 用异步循环读
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$url = $null
$pastePromptSeen = $false

# stderr 异步泵
$stderrTask = [System.Threading.Tasks.Task]::Run([Action] {
    $err = $p.StandardError
    while (-not $err.EndOfStream) {
        $line = $err.ReadLineAsync().GetAwaiter().GetResult()
        if ($null -eq $line) { break }
        Log "ERR" $line
    }
})

while ($sw.Elapsed.TotalSeconds -lt 45) {
    $line = $p.StandardOutput.ReadLineAsync().GetAwaiter().GetResult()
    if ($null -eq $line) { break }
    Log "OUT" $line
    if ($line -match 'https://(claude\.com/cai|claude\.ai)/oauth/authorize\S+|https://claude\.com/cai/oauth/authorize\S+') {
        if ($url -eq $null) { $url = $Matches[0] }
    }
    if ($line -match 'Paste code') { $pastePromptSeen = $true; break }
}

if ($url) {
    Write-Host "AUTH_URL_BEGIN"
    Write-Host $url
    Write-Host "AUTH_URL_END"
} else {
    Write-Host "NO URL CAPTURED (pastePromptSeen=$pastePromptSeen)"
}

# 等待授权码文件（30 分钟）
$code = $null
$sw.Restart()
while ($sw.Elapsed.TotalMinutes -lt 30 -and $url -ne $null) {
    if (Test-Path $codeFile) { $code = (Get-Content $codeFile -Raw).Trim(); break }
    Start-Sleep -Milliseconds 500
}

if ($code) {
    Log "INJ" "injecting code (masked: $($code.Substring(0,10))...)"
    $p.StandardInput.WriteLine($code)
    $p.StandardInput.Flush()
} else {
    if ($url -ne $null) { Log "INJ" "TIMEOUT WAITING FOR CODE" }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $stderrTask.Wait(3000) | Out-Null
    $debug.ToString() | Out-File $debugFile -Encoding utf8
    exit 1
}

# 读取交换结果直到退出
$sw.Restart()
while ($sw.Elapsed.TotalMinutes -lt 5 -and -not $p.HasExited) {
    $line = $p.StandardOutput.ReadLineAsync().GetAwaiter().GetResult()
    if ($null -eq $line) { break }
    Log "OUT" $line
}
$p.WaitForExit(30000) | Out-Null
try { $debug.AppendLine("EXIT: $($p.ExitCode)") | Out-Null } catch {}
$debug.ToString() | Out-File $debugFile -Encoding utf8
Write-Host "DRIVER DONE (exit $($p.ExitCode))"
