param(
    [int]$Port = 5055
)

# 停止后台网关进程
$connections = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue
if ($null -eq $connections) {
    Write-Host "No process listening on port $Port; nothing to stop."
    return
}

$owners = $connections | Select-Object -ExpandProperty OwningProcess -Unique
foreach ($pidOwner in $owners) {
    if ($pidOwner -and $pidOwner -ne 0) {
        Stop-Process -Id $pidOwner -Force -ErrorAction SilentlyContinue
        Write-Host "Stopped gateway process (PID $pidOwner) on port $Port" -ForegroundColor Green
    }
}
