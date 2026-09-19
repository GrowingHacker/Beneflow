# Uninstall Beneflow.Api service and clean up
# Usage: run in elevated PowerShell, from the unzip root / repo root
#   Set-ExecutionPolicy -Scope Process Bypass -Force
#   .\scripts\uninstall-service.ps1
# 路径相对脚本自身位置推导（发行包布局：publish\ 与 scripts\ 同级）。

param(
    [string]$ServiceName = "Beneflow.Api",
    [string]$PublishDir  = "",             # 留空 = 自动识别（发行包布局优先，其次源码仓库布局）
    [switch]$KeepCerts,    # set to skip cert cleanup
    [switch]$KeepPublish  # set to skip publish dir cleanup
)

$ErrorActionPreference = "Continue"

# 发布目录：默认「脚本上一级目录下的 publish\」（与 scripts\ 同级），可 -PublishDir 指定别处。
# 删的就是这个已发布目录（发行包里就是 .\publish\）。
if (-not $PublishDir) {
    $PublishDir = Join-Path (Split-Path -Parent $PSScriptRoot) "publish"
}
Write-Host "发布目录：$PublishDir" -ForegroundColor Cyan

Write-Host "=== 1. 停止服务 ===" -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
        for ($i = 0; $i -lt 10; $i++) {
            if (-not (Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Seconds 1
        }
    }
    Write-Host "服务已停止" -ForegroundColor Green
} else {
    Write-Host "未找到服务，跳过" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 2. 删除服务 ===" -ForegroundColor Cyan
if ($svc) {
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "服务已删除" -ForegroundColor Green
    } else {
        Write-Host "sc delete 失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
    }
} else {
    Write-Host "未找到服务，跳过" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 3. 移除防火墙规则 ===" -ForegroundColor Cyan
$rules = Get-NetFirewallRule -DisplayName "Beneflow.Api*" -ErrorAction SilentlyContinue
if ($rules) {
    $rules | Remove-NetFirewallRule
    Write-Host "已移除 $($rules.Count) 条防火墙规则" -ForegroundColor Green
} else {
    Write-Host "没有找到 Beneflow 的防火墙规则" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 4. 结束残留进程 ===" -ForegroundColor Cyan
$proc = Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue
if ($proc) {
    $proc | Stop-Process -Force
    Write-Host "已结束残留进程（PID：$($proc.Id -join ',')）" -ForegroundColor Green
} else {
    Write-Host "没有残留进程" -ForegroundColor Yellow
}

if (-not $KeepPublish) {
    Write-Host ""
    Write-Host "=== 5. 删除发布目录 ===" -ForegroundColor Cyan
    if (Test-Path $PublishDir) {
        Remove-Item -Path $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $PublishDir) {
            Write-Host "删除失败（可能仍被占用）：$PublishDir" -ForegroundColor Red
            Write-Host "请手工处理：explorer $PublishDir 然后删除" -ForegroundColor Yellow
        } else {
            Write-Host "已删除：$PublishDir" -ForegroundColor Green
        }
    } else {
        Write-Host "没有找到发布目录：$PublishDir" -ForegroundColor Yellow
    }
}

if (-not $KeepCerts) {
    Write-Host ""
    Write-Host "=== 6. 清理 TLS 证书 ===" -ForegroundColor Cyan

    # LocalSystem account cert dir (used when running as Windows Service)
    $lsCertDir = "C:\Windows\System32\config\systemprofile\AppData\Local\Beneflow\tls"
    if (Test-Path $lsCertDir) {
        Remove-Item $lsCertDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "已删除 LocalSystem 的证书目录：$lsCertDir" -ForegroundColor Green
    }

    # Current user cert dir (used when running via dotnet run)
    $cuCertDir = Join-Path $env:LOCALAPPDATA "Beneflow\tls"
    if (Test-Path $cuCertDir) {
        Remove-Item $cuCertDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "已删除 CurrentUser 的证书目录：$cuCertDir" -ForegroundColor Green
    }

    # Leaf cert in LocalMachine\My
    Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -match "Beneflow-Server" } |
        ForEach-Object {
            Remove-Item "Cert:\LocalMachine\My\$($_.Thumbprint)" -Force -ErrorAction SilentlyContinue
            Write-Host "已移除叶子证书：$($_.Thumbprint)" -ForegroundColor Green
        }

    # Root CA in LocalMachine\Root (may require special privileges)
    $rootCAs = Get-ChildItem Cert:\LocalMachine\Root |
               Where-Object { $_.Subject -match "Beneflow Local Root CA" }
    if ($rootCAs) {
        foreach ($ca in $rootCAs) {
            try {
                Remove-Item "Cert:\LocalMachine\Root\$($ca.Thumbprint)" -Force -ErrorAction Stop
                Write-Host "已移除根证书：$($ca.Thumbprint)" -ForegroundColor Green
            } catch {
                Write-Host "无法移除根证书（Windows 有保护）：$($ca.Thumbprint)" -ForegroundColor Yellow
                Write-Host "  手工删除：certlm.msc -> 受信任的根证书颁发机构 -> 找到 'Beneflow Local Root CA' -> 删除" -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host "没有找到根证书" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "=== 卸载完成 ===" -ForegroundColor Green
Write-Host ""
Write-Host "复核：" -ForegroundColor Cyan
Write-Host "  服务：      $(if (Get-Service $ServiceName -EA SilentlyContinue) {'仍然存在'} else {'已移除'})"
Write-Host "  进程：      $(if (Get-Process -Name 'Beneflow.Api' -EA SilentlyContinue) {'仍在运行'} else {'已停止'})"
Write-Host "  端口 5000：$(if (netstat -ano | Select-String ':5000.*LISTENING') {'仍被占用'} else {'空闲'})"
Write-Host "  端口 5001：$(if (netstat -ano | Select-String ':5001.*LISTENING') {'仍被占用'} else {'空闲'})"
Write-Host ""
Write-Host "使用的参数：" -ForegroundColor Cyan
Write-Host "  KeepPublish：$KeepPublish"
Write-Host "  KeepCerts：  $KeepCerts"
