# Deploy Beneflow.Api as a pure Kestrel Windows Service
# Usage: run in elevated PowerShell
# Prerequisite: dotnet publish output at D:\Beneflow\publish

param(
    [string]$ServiceName = "Beneflow.Api",
    [string]$PublishDir  = "D:\Beneflow\publish",
    [int[]]$Ports        = @(5000, 5001)
)

$ErrorActionPreference = "Stop"

Write-Host "=== 1. Verify publish output ===" -ForegroundColor Cyan
$exe = Join-Path $PublishDir "Beneflow.Api.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Not found: $exe" -ForegroundColor Red
    Write-Host "Run first:" -ForegroundColor Yellow
    Write-Host '  dotnet publish D:\Beneflow\src\Beneflow.Api\Beneflow.Api.csproj -c Release -r win-x64 --self-contained false -o D:\Beneflow\publish' -ForegroundColor Yellow
    exit 1
}
Write-Host "Entry: $exe"

Write-Host ""
Write-Host "=== 2. Stop and delete existing service (if any) ===" -ForegroundColor Cyan
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq "Running") {
        Write-Host "Stopping old service..." -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Old service deleted" -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}

Write-Host ""
Write-Host "=== 3. Register as Windows Service ===" -ForegroundColor Cyan
$binPath = "`"$exe`""
sc.exe create $ServiceName binPath= $binPath start= auto | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "sc create failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}

Write-Host "ServiceName:  $ServiceName"
Write-Host "Description:  Beneflow inventory/sales management system"
Write-Host "StartupType:  Automatic"
Write-Host "Executable:   $exe"

sc.exe description $ServiceName "Beneflow management system - Kestrel HTTP 5000 / HTTPS 5001" | Out-Null

Write-Host ""
Write-Host "=== 4. Configure crash recovery (auto restart) ===" -ForegroundColor Cyan
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Host "Configured: 1st crash 5s / 2nd 10s / 3rd 30s auto-restart, 24h no-fail resets counter"

Write-Host ""
Write-Host "=== 5. Start service ===" -ForegroundColor Cyan
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3
$svc = Get-Service -Name $ServiceName
Write-Host "Service status: $($svc.Status)" -ForegroundColor Green

Write-Host ""
Write-Host "=== 6. Firewall rules for 5000/5001 (all profiles) ===" -ForegroundColor Cyan
foreach ($port in $Ports) {
    Get-NetFirewallRule -DisplayName "Beneflow.Api $port*" -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "Beneflow.Api TCP $port" -Direction Inbound `
        -LocalPort $port -Protocol TCP -Action Allow -Profile Any | Out-Null
    New-NetFirewallRule -DisplayName "Beneflow.Api UDP $port" -Direction Inbound `
        -LocalPort $port -Protocol UDP -Action Allow -Profile Any | Out-Null
    Write-Host "Opened $port (TCP+UDP, all profiles)"
}

Write-Host ""
Write-Host "=== 7. Health check ===" -ForegroundColor Cyan
Start-Sleep -Seconds 3

$proc = Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    Write-Host "Process running: PID=$($proc.Id), StartTime=$($proc.StartTime)" -ForegroundColor Green
} else {
    Write-Host "Process not running, likely startup failure. Check Event Viewer -> Windows Logs -> Application -> Source 'Beneflow.Api'" -ForegroundColor Red
}

# Use curl.exe instead of Invoke-WebRequest - PS5.x has a bug where ScriptBlock
# cert validation callback fails in async TLS thread ("no runspace available")
Write-Host "HTTP 5000 self-test:" -NoNewline
$http = & curl.exe -s -o NUL -w "%{http_code} %{time_total}s" http://127.0.0.1:5000/ 2>&1
if ($http -match "^200") {
    Write-Host " OK ($http)" -ForegroundColor Green
} else {
    Write-Host " FAILED ($http)" -ForegroundColor Red
}

Write-Host "HTTPS 5001 self-test:" -NoNewline
$https = & curl.exe -k -s -o NUL -w "%{http_code} %{time_total}s ssl_verify=%{ssl_verify_result}" https://127.0.0.1:5001/ 2>&1
if ($https -match "^200") {
    Write-Host " OK ($https)" -ForegroundColor Green
} else {
    Write-Host " FAILED ($https)" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Green
Write-Host "ServiceName: $ServiceName"
Write-Host "Executable: $exe"
Write-Host "WorkDir:    $PublishDir"
Write-Host ""
Write-Host "Common commands:" -ForegroundColor Cyan
Write-Host "  Start:    Start-Service $ServiceName"
Write-Host "  Stop:     Stop-Service $ServiceName"
Write-Host "  Restart:  Restart-Service $ServiceName"
Write-Host "  Status:   Get-Service $ServiceName"
Write-Host "  Logs:     Get-WinEvent -LogName Application -ProviderName 'Beneflow.Api' -MaxEvents 50"
Write-Host "  Uninstall: Stop-Service $ServiceName; sc.exe delete $ServiceName"
Write-Host ""
Write-Host "Local:   http://localhost:5000  /  https://localhost:5001"
Write-Host "LAN:     http://<LAN-IP>:5000   /   https://<LAN-IP>:5001"
