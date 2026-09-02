# Redeploy Beneflow.Api service: rebuild + stop + replace + start + verify
# Usage: run in elevated PowerShell
#   Set-ExecutionPolicy -Scope Process Bypass -Force
#   .\update-service.ps1

param(
    [string]$ServiceName = "Beneflow.Api",
    [string]$ProjectPath = "D:\Beneflow\src\Beneflow.Api\Beneflow.Api.csproj",
    [string]$PublishDir  = "D:\Beneflow\publish"
)

$ErrorActionPreference = "Stop"

Write-Host "=== 1. Stop service (release DLL lock) ===" -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Stop-Service -Name $ServiceName -Force
    # Wait up to 10s for process to actually exit
    for ($i = 0; $i -lt 10; $i++) {
        if (-not (Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
    Write-Host "Service stopped" -ForegroundColor Green
} else {
    Write-Host "Service not running, skip stop" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 2. dotnet publish (overwrite publish dir) ===" -ForegroundColor Cyan
& dotnet publish $ProjectPath -c Release -r win-x64 --self-contained false -o $PublishDir --no-restore 2>&1 |
    Select-Object -Last 5
if ($LASTEXITCODE -ne 0) {
    # Retry with restore if --no-restore failed
    Write-Host "Publish with --no-restore failed, retry with restore..." -ForegroundColor Yellow
    & dotnet publish $ProjectPath -c Release -r win-x64 --self-contained false -o $PublishDir 2>&1 |
        Select-Object -Last 5
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "dotnet publish failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit 1
}
Write-Host "Publish done" -ForegroundColor Green

Write-Host ""
Write-Host "=== 3. Start service ===" -ForegroundColor Cyan
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3
$svc = Get-Service -Name $ServiceName
Write-Host "Service status: $($svc.Status)" -ForegroundColor Green

Write-Host ""
Write-Host "=== 4. Health check ===" -ForegroundColor Cyan
$proc = Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    Write-Host "Process: PID=$($proc.Id), StartTime=$($proc.StartTime)" -ForegroundColor Green
} else {
    Write-Host "Process not running!" -ForegroundColor Red
    Write-Host "Check Event Viewer -> Application -> Source 'Beneflow.Api' for startup errors" -ForegroundColor Yellow
    exit 1
}

# curl.exe instead of Invoke-WebRequest (PS5.x ScriptBlock async TLS bug)
Write-Host "HTTP 5000:" -NoNewline
$http = & curl.exe -s -o NUL -w "%{http_code} %{time_total}s" http://127.0.0.1:5000/ 2>&1
if ($http -match "^200") {
    Write-Host " OK ($http)" -ForegroundColor Green
} else {
    Write-Host " FAILED ($http)" -ForegroundColor Red
}

Write-Host "HTTPS 5001:" -NoNewline
$https = & curl.exe -k -s -o NUL -w "%{http_code} %{time_total}s ssl_verify=%{ssl_verify_result}" https://127.0.0.1:5001/ 2>&1
if ($https -match "^200") {
    Write-Host " OK ($https)" -ForegroundColor Green
} else {
    Write-Host " FAILED ($https)" -ForegroundColor Red
}

# Show last 10 log lines from Serilog file
Write-Host ""
Write-Host "=== Last log entries ===" -ForegroundColor Cyan
$logDir = Join-Path $PublishDir "logs"
if (Test-Path $logDir) {
    $latest = Get-ChildItem $logDir | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        Get-Content $latest.FullName -Tail 10 |
            ForEach-Object {
                # Decode garbled UTF-8 Chinese if needed (PS5.x console default GBK)
                try {
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($_)
                    [System.Text.Encoding]::UTF8.GetString($bytes)
                } catch { $_ }
            }
    }
} else {
    Write-Host "(no log dir)"
}

Write-Host ""
Write-Host "=== Redeploy complete ===" -ForegroundColor Green
