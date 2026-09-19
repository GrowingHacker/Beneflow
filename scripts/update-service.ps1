# Update an already-deployed Beneflow.Api service from an existing publish folder
# Usage: run in elevated PowerShell, from the unzip root
#   Set-ExecutionPolicy -Scope Process Bypass -Force
#   .\scripts\update-service.ps1
#
#   本脚本**只面向已经发布好的 publish 文件夹**（不负责编译、不调用 dotnet publish）。
#   发行包布局：<解压根目录>\publish\（已发布产物） + <解压根目录>\scripts\（本脚本）
#   更新程序的步骤：先把新版本的发布产物覆盖进 .\publish\，再运行本脚本。
#
# 流程：停服务 → 应用数据库迁移 → 启服务 → 健康检查 → 打印最新日志

param(
    [string]$ServiceName = "Beneflow.Api",
    [string]$PublishDir  = "",              # 留空 = 脚本上一级目录下的 publish\（与 scripts\ 同级）
    [switch]$SkipMigrate                    # 跳过数据库迁移（Beneflow.Api.exe --migrate）
)

$ErrorActionPreference = "Stop"

Write-Host "=== 0. 环境预检 ===" -ForegroundColor Cyan

# 停/启服务要求提权
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限。" -ForegroundColor Red
    Write-Host "请以管理员身份打开 PowerShell，然后重新运行本脚本。" -ForegroundColor Yellow
    exit 1
}

# 发布目录：默认取「脚本上一级目录下的 publish\」，可 -PublishDir 指定别处
if (-not $PublishDir) {
    $PublishDir = Join-Path (Split-Path -Parent $PSScriptRoot) "publish"
}
$exe = Join-Path $PublishDir "Beneflow.Api.exe"
if (-not (Test-Path $exe)) {
    Write-Host "找不到：$exe" -ForegroundColor Red
    Write-Host "本脚本更新的是**已经发布好的**目录 —— 它不负责编译，什么都不构建。" -ForegroundColor Yellow
    Write-Host "期望的目录结构（路径相对「解压根目录」）：" -ForegroundColor Yellow
    Write-Host "  .\publish\Beneflow.Api.exe    <- 发布产物放这里"
    Write-Host "  .\scripts\update-service.ps1  <- 本脚本"
    Write-Host "如果发布目录在别处，显式传进来：" -ForegroundColor Yellow
    Write-Host "  .\scripts\update-service.ps1 -PublishDir .\some\where"
    exit 1
}
Write-Host "发布目录：$PublishDir" -ForegroundColor Green

# 服务必须已注册过：本脚本不创建服务，首次部署用 deploy-kestrel-service.ps1
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "服务 '$ServiceName' 尚未安装。" -ForegroundColor Red
    Write-Host "请改跑首次部署脚本：" -ForegroundColor Yellow
    Write-Host "  .\scripts\deploy-kestrel-service.ps1"
    exit 1
}

Write-Host ""
Write-Host "=== 1. 停止服务（释放 DLL 占用） ===" -ForegroundColor Cyan
if ($svc.Status -eq "Running") {
    Stop-Service -Name $ServiceName -Force
    # Wait up to 10s for process to actually exit
    for ($i = 0; $i -lt 10; $i++) {
        if (-not (Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }
    Write-Host "服务已停止" -ForegroundColor Green
} else {
    Write-Host "服务未在运行，跳过停止" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 2. 应用数据库迁移 ===" -ForegroundColor Cyan
if ($SkipMigrate) {
    Write-Host "已跳过（-SkipMigrate）。库结构必须已经是最新的。" -ForegroundColor Yellow
} else {
    # 新版本可能带来新迁移：在发布目录里执行 exe，程序自己升级 schema 后退出（幂等）
    Write-Host "正在执行：Beneflow.Api.exe --migrate（工作目录：$PublishDir）"
    Push-Location $PublishDir
    try {
        & ".\Beneflow.Api.exe" --migrate 2>&1 | Select-Object -Last 8
    } finally {
        Pop-Location
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "数据库迁移失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
        Write-Host "排查方向：" -ForegroundColor Yellow
        Write-Host "  1. 用 appsettings 里的连接串能连上 SQL Server 吗？"
        Write-Host "  2. 那个登录名有 CREATE DATABASE / CREATE TABLE 权限吗？"
        Write-Host "  3. 完整日志：$PublishDir\logs\"
        exit 1
    }
    Write-Host "数据库已是最新" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== 3. 启动服务 ===" -ForegroundColor Cyan
try {
    Start-Service -Name $ServiceName
} catch {
    Write-Host "Start-Service 报错：$($_.Exception.Message)" -ForegroundColor Red
}
Start-Sleep -Seconds 5
$svc = Get-Service -Name $ServiceName
Write-Host "服务状态：$($svc.Status)" -ForegroundColor Green

Write-Host ""
Write-Host "=== 4. 健康检查 ===" -ForegroundColor Cyan
$proc = Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    Write-Host "进程：PID=$($proc.Id)，启动时间=$($proc.StartTime)" -ForegroundColor Green
} else {
    Write-Host "进程没有在运行！" -ForegroundColor Red
    Write-Host "请查事件查看器 -> 应用程序 -> 来源 'Beneflow.Api' 看启动错误" -ForegroundColor Yellow
    Write-Host "或者直接前台运行看报错：$exe" -ForegroundColor Yellow
}

# curl.exe instead of Invoke-WebRequest (PS5.x ScriptBlock async TLS bug)
Write-Host "HTTP 5000：" -NoNewline
$http = & curl.exe -s -o NUL -w "%{http_code} %{time_total}s" http://127.0.0.1:5000/ 2>&1
if ($http -match "^200") {
    Write-Host " 通过（$http）" -ForegroundColor Green
} else {
    Write-Host " 失败（$http）" -ForegroundColor Red
}

Write-Host "HTTPS 5001：" -NoNewline
$https = & curl.exe -k -s -o NUL -w "%{http_code} %{time_total}s ssl_verify=%{ssl_verify_result}" https://127.0.0.1:5001/ 2>&1
if ($https -match "^200") {
    Write-Host " 通过（$https）" -ForegroundColor Green
} else {
    Write-Host " 失败（$https）" -ForegroundColor Red
}

# Show last 10 log lines from Serilog file
Write-Host ""
Write-Host "=== 最近的日志 ===" -ForegroundColor Cyan
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
    Write-Host "（没有日志目录）"
}

Write-Host ""
Write-Host "=== 更新完成 ===" -ForegroundColor Green
Write-Host "可执行文件：$exe"
Write-Host "工作目录：$PublishDir"
