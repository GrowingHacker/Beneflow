# Uninstall Beneflow.Api service and clean up
# Usage: run in elevated PowerShell
#   Set-ExecutionPolicy -Scope Process Bypass -Force
#   .\uninstall-service.ps1

param(
    [string]$ServiceName = "Beneflow.Api",
    [string]$PublishDir  = "D:\Beneflow\publish",
    [switch]$KeepCerts,    # set to skip cert cleanup
    [switch]$KeepPublish  # set to skip publish dir cleanup
)

$ErrorActionPreference = "Continue"

Write-Host "=== 1. Stop service ===" -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
        for ($i = 0; $i -lt 10; $i++) {
            if (-not (Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Seconds 1
        }
    }
    Write-Host "Service stopped" -ForegroundColor Green
} else {
    Write-Host "Service not found, skip" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 2. Delete service ===" -ForegroundColor Cyan
if ($svc) {
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Service deleted" -ForegroundColor Green
    } else {
        Write-Host "sc delete failed (exit $LASTEXITCODE)" -ForegroundColor Red
    }
} else {
    Write-Host "Service not found, skip" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 3. Remove firewall rules ===" -ForegroundColor Cyan
$rules = Get-NetFirewallRule -DisplayName "Beneflow.Api*" -ErrorAction SilentlyContinue
if ($rules) {
    $rules | Remove-NetFirewallRule
    Write-Host "Removed $($rules.Count) firewall rules" -ForegroundColor Green
} else {
    Write-Host "No Beneflow firewall rules found" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 4. Kill any leftover processes ===" -ForegroundColor Cyan
$proc = Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue
if ($proc) {
    $proc | Stop-Process -Force
    Write-Host "Killed leftover process (PID: $($proc.Id -join ','))" -ForegroundColor Green
} else {
    Write-Host "No leftover process" -ForegroundColor Yellow
}

if (-not $KeepPublish) {
    Write-Host ""
    Write-Host "=== 5. Delete publish directory ===" -ForegroundColor Cyan
    if (Test-Path $PublishDir) {
        Remove-Item -Path $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $PublishDir) {
            Write-Host "Failed to delete (may still be locked): $PublishDir" -ForegroundColor Red
            Write-Host "Try manually: explorer $PublishDir then delete" -ForegroundColor Yellow
        } else {
            Write-Host "Deleted: $PublishDir" -ForegroundColor Green
        }
    } else {
        Write-Host "Publish dir not found: $PublishDir" -ForegroundColor Yellow
    }
}

if (-not $KeepCerts) {
    Write-Host ""
    Write-Host "=== 6. Clean up TLS certificates ===" -ForegroundColor Cyan

    # LocalSystem account cert dir (used when running as Windows Service)
    $lsCertDir = "C:\Windows\System32\config\systemprofile\AppData\Local\Beneflow\tls"
    if (Test-Path $lsCertDir) {
        Remove-Item $lsCertDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Deleted LocalSystem cert dir: $lsCertDir" -ForegroundColor Green
    }

    # Current user cert dir (used when running via dotnet run)
    $cuCertDir = Join-Path $env:LOCALAPPDATA "Beneflow\tls"
    if (Test-Path $cuCertDir) {
        Remove-Item $cuCertDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Deleted CurrentUser cert dir: $cuCertDir" -ForegroundColor Green
    }

    # Leaf cert in LocalMachine\My
    Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -match "Beneflow-Server" } |
        ForEach-Object {
            Remove-Item "Cert:\LocalMachine\My\$($_.Thumbprint)" -Force -ErrorAction SilentlyContinue
            Write-Host "Removed leaf cert: $($_.Thumbprint)" -ForegroundColor Green
        }

    # Root CA in LocalMachine\Root (may require special privileges)
    $rootCAs = Get-ChildItem Cert:\LocalMachine\Root |
               Where-Object { $_.Subject -match "Beneflow Local Root CA" }
    if ($rootCAs) {
        foreach ($ca in $rootCAs) {
            try {
                Remove-Item "Cert:\LocalMachine\Root\$($ca.Thumbprint)" -Force -ErrorAction Stop
                Write-Host "Removed Root CA: $($ca.Thumbprint)" -ForegroundColor Green
            } catch {
                Write-Host "Cannot remove Root CA (Windows protects it): $($ca.Thumbprint)" -ForegroundColor Yellow
                Write-Host "  Manual removal: certlm.msc -> Trusted Root CAs -> find 'Beneflow Local Root CA' -> delete" -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host "No Root CA found" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "=== Uninstall complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Verification:" -ForegroundColor Cyan
Write-Host "  Service:    $(if (Get-Service $ServiceName -EA SilentlyContinue) {'STILL EXISTS'} else {'removed'})"
Write-Host "  Process:    $(if (Get-Process -Name 'Beneflow.Api' -EA SilentlyContinue) {'STILL RUNNING'} else {'stopped'})"
Write-Host "  Port 5000:  $(if (netstat -ano | Select-String ':5000.*LISTENING') {'still in use'} else {'free'})"
Write-Host "  Port 5001:  $(if (netstat -ano | Select-String ':5001.*LISTENING') {'still in use'} else {'free'})"
Write-Host ""
Write-Host "Options used:" -ForegroundColor Cyan
Write-Host "  KeepPublish: $KeepPublish"
Write-Host "  KeepCerts:   $KeepCerts"
