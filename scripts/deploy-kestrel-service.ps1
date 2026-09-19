# Deploy Beneflow.Api as a pure Kestrel Windows Service
# Usage: run in elevated PowerShell, from the unzip root
#
#   本脚本**只面向已经发布好的 publish 文件夹**（不负责编译、不调用 dotnet publish）。
#
#   发行包（zip 解压后 publish\ 与 scripts\ 同级）：
#       <解压根目录>\
#       ├── publish\        已发布好的产物（Beneflow.Api.exe / wwwroot / appsettings.example.json）
#       └── scripts\        本脚本 + setup-sqlserver.ps1 + _common.ps1（三个必须放在同一目录）
#
#       cd <解压根目录>
#       .\scripts\deploy-kestrel-service.ps1
#
#   发布目录默认取「脚本上一级目录下的 publish\」，可用 -PublishDir 指定别处；
#   路径全部从脚本自身位置推导，所以整包解压到任意盘符/任意目录都能用。
#
#   数据库初始化由 `Beneflow.Api.exe --migrate` 完成（免源码、免 SDK，见脚本第 3 步）。
#
#   ⚠️ 第 2 步会写 publish\appsettings.Production.json：文件不存在就由
#      appsettings.example.json **复制**生成（模板保留在包里），再把「应用猜不出来的键」
#      补齐 —— 连接串 + Jwt:Secret + Security:AesKey。写完立刻校验，带着 <占位符> 绝不往下走。
#      实现见 scripts\_common.ps1 的 Write-ProductionConfig，与 setup-sqlserver.ps1 共用。
#
#   ⚠️ 第 2b 步会把这条连接串**真连一次**，不通过就停下来问人，问不到就不往下走。
#      这一步专治「客户没跑 setup-sqlserver.ps1」：那时连接串是按探测到的实例名**拼**出来的猜测，
#      拼错了要到第 3 步才炸（一堆 EF 堆栈），拼对了也可能因为服务身份没授权而在第 8 步死掉。
#      触发条件是「验不过」，不是「还是占位符」—— Write-ProductionConfig 一定会把占位符填掉。
#      实现见 scripts\_common.ps1 的 Resolve-UsableConnectionString / Test-BeneflowConnectionString。
#      -ConnectionString 可跳过提示；-NonInteractive 让脚本在验不过时直接退出而不是等人输入。
#
# 流程：校验产物 → 准备并校验配置 → **验证连接串** → 初始化数据库 → 注册服务 → 防火墙 → 健康检查

param(
    [string]$ServiceName = "Beneflow.Api",
    [string]$PublishDir  = "",              # 留空 = 脚本上一级目录下的 publish\（与 scripts\ 同级）
    [string]$InstanceName = "",             # 留空 = 自动探测本机实例；仅在「Production 文件不存在、需要现拼连接串」时用得上
    [int[]]$Ports        = @(5000, 5001),
    [switch]$SkipMigrate,                   # 跳过数据库初始化（Beneflow.Api.exe --migrate）
    [switch]$ForceConfig,                   # 覆盖已存在的连接串 / 密钥（会换掉 Jwt:Secret，等于把所有人踢下线）
    [string]$ConnectionString = "",         # 留空 = 先验配置里那条；验不过才问人。非交互场景直接把串给这里
    [switch]$NonInteractive                 # 连接串验不过就直接退出，绝不停下来等输入（计划任务用）
)

$ErrorActionPreference = "Stop"

# JSON 路径读写 + 生产配置生成，与 setup-sqlserver.ps1 共用同一份实现。
# 先判存在再 dot-source：少了它，PowerShell 抛的是「找不到路径」这种对客户毫无意义的错。
$commonPs1 = Join-Path $PSScriptRoot "_common.ps1"
if (-not (Test-Path $commonPs1)) {
    Write-Host "缺少必需文件：$commonPs1" -ForegroundColor Red
    Write-Host "它必须与本脚本放在同一目录（scripts\_common.ps1）。" -ForegroundColor Yellow
    Write-Host "请重新解压发行包 —— 此前解压出来的 scripts\ 目录不完整。" -ForegroundColor Yellow
    exit 1
}
. $commonPs1

Write-Host "=== 0. 环境预检 ===" -ForegroundColor Cyan

# sc.exe 注册服务、防火墙规则、服务控制都要求提权，早失败比中途失败好排查
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限。" -ForegroundColor Red
    Write-Host "请以管理员身份打开 PowerShell，然后重新运行本脚本。" -ForegroundColor Yellow
    exit 1
}
Write-Host "已以管理员身份运行" -ForegroundColor Green

# 发布目录：默认取「脚本上一级目录下的 publish\」（发行包布局：publish\ 与 scripts\ 同级）；
# 路径全部相对脚本自身位置推导，与盘符、当前工作目录都无关。
if (-not $PublishDir) {
    $PublishDir = Join-Path (Split-Path -Parent $PSScriptRoot) "publish"
}

Write-Host ""
Write-Host "=== 1. 校验发布产物 ===" -ForegroundColor Cyan
$exe = Join-Path $PublishDir "Beneflow.Api.exe"
if (-not (Test-Path $exe)) {
    Write-Host "找不到：$exe" -ForegroundColor Red
    Write-Host "本脚本部署的是**已经发布好的**目录 —— 它不负责编译，什么都不构建。" -ForegroundColor Yellow
    Write-Host "期望的目录结构（路径相对「解压根目录」）：" -ForegroundColor Yellow
    Write-Host "  .\publish\Beneflow.Api.exe   <- 发布产物放这里"
    Write-Host "  .\scripts\deploy-kestrel-service.ps1   <- 本脚本"
    Write-Host "如果发布目录在别处，显式传进来：" -ForegroundColor Yellow
    Write-Host "  .\scripts\deploy-kestrel-service.ps1 -PublishDir .\some\where"
    exit 1
}
Write-Host "可执行文件：$exe"

Write-Host ""
Write-Host "=== 2. 准备并校验配置 ===" -ForegroundColor Cyan

# 最后一公里：把 publish\appsettings.Production.json 准备到「能直接启动」的程度。
# 缺文件就由 appsettings.example.json 复制生成（模板留在包里，重装还要用），
# 再把应用猜不出来的键补齐：连接串 + Jwt:Secret + Security:AesKey。
# 不合格就停在这里 —— 带着 <占位符> 进第 3 步，客户看到的会是一堆 EF 堆栈。
#
# 实例名默认自动探测。注意它只在「Production 不存在、要现拼连接串」时才起作用；
# 正常流程里 setup-sqlserver.ps1 已经写好连接串，这里是兜底。
$prodCfg = Join-Path $PublishDir "appsettings.Production.json"
$resolvedInstance = Resolve-SqlInstanceName -InstanceName $InstanceName
if ($resolvedInstance) {
    Write-Host "SQL Server 实例：$resolvedInstance"
} elseif (-not (Test-Path $prodCfg)) {
    Write-Host "没探测到 SQL Server 实例，而且 appsettings.Production.json 还不存在。" -ForegroundColor Yellow
    Write-Host "即将生成的连接串会假定是**默认实例**（Server=.）。" -ForegroundColor Yellow
    Write-Host "建议先跑 setup-sqlserver.ps1，或者用 -InstanceName <实例名> 指定。" -ForegroundColor Yellow
}

$config = Write-ProductionConfig -PublishDir $PublishDir -InstanceName $resolvedInstance -Force:$ForceConfig
if (-not $config.Ok) {
    Write-Host ""
    Write-Host "生产配置不可用：" -ForegroundColor Red
    foreach ($p in $config.Problems) { Write-Host "  - $p" -ForegroundColor Red }
    Write-Host ""
    Write-Host "可以手工把文件改好，或者先跑 setup-sqlserver.ps1 —— 它会给您生成一份：" -ForegroundColor Yellow
    Write-Host "  .\scripts\setup-sqlserver.ps1"
    exit 1
}
Write-Host "配置校验通过" -ForegroundColor Green

# appsettings.json 是**开发配置**（含 dev 连接串 / dev Jwt 密钥 / dev AesKey），
# 发布产物里默认带着它。Production 现在携带完整键结构，所以删掉它不会让配置缺键。
# 交付前建议删掉，否则客户机上会同时存在一份含开发密钥的配置。
$devCfg = Join-Path $PublishDir "appsettings.json"
if (Test-Path $devCfg) {
    Write-Host "注意：$devCfg 是**开发配置**，会随产物一起被交出去。" -ForegroundColor Yellow
    Write-Host "      Production 会逐键覆盖它，但交付前删掉更稳妥。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 2b. 验证数据库连接（必须通过才继续） ===" -ForegroundColor Cyan

# 这一步专治「客户没跑 setup-sqlserver.ps1」这条路。
#
# ⚠️ 触发条件**不是**「连接串还是占位符」—— Write-ProductionConfig 一定会把占位符填掉。
#    真正的风险是它填进去的那条串是按探测到的实例名**拼**出来的猜测，从来没人验过：
#      · 实例名猜错            -> 第 3 步 --migrate 炸一堆 EF 堆栈，客户看不出是实例名的问题
#      · 猜对了但 SYSTEM 没被授权 -> 第 8 步服务起来又立刻死，症状是「用户登录失败」
#    两种都要在这里拦住，所以这里做的是**真连一次**，不是拿字符串比划。
$conn = Resolve-UsableConnectionString -PublishDir $PublishDir `
            -ConnectionString $ConnectionString -NonInteractive:$NonInteractive

if (-not $conn.Usable) {
    Write-Host ""
    Write-Host "连接串验证未通过 —— 在第 3 步之前停下。" -ForegroundColor Red
    Write-Host ""
    Write-Host "三条出路：" -ForegroundColor Yellow
    Write-Host "  1. 先把数据库这一层准备好，再重跑本脚本："
    Write-Host "       .\scripts\setup-sqlserver.ps1"
    Write-Host "  2. 直接把连接串交给本脚本："
    Write-Host '       .\scripts\deploy-kestrel-service.ps1 -ConnectionString "Server=.\SQLEXPRESS;Database=BeneflowDb;Trusted_Connection=True;Encrypt=False"'
    Write-Host "  3. 手工改 $PublishDir\appsettings.Production.json 的 ConnectionStrings:Default"
    exit 1
}
if ($conn.Skipped) {
    Write-Host "按你的要求，在连接串未经验证的情况下继续。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 3. 初始化数据库（不需要源码） ===" -ForegroundColor Cyan
if ($SkipMigrate) {
    Write-Host "已跳过（-SkipMigrate）。库结构必须已经存在。" -ForegroundColor Yellow
} else {
    # 迁移类编译在 Beneflow.Api.dll 内，exe 自己就能建库建表并播种演示数据（幂等）。
    # 在发布目录里执行 exe：程序虽然会把 ContentRoot 固定为自身所在目录，但显式切工作目录
    # 能让日志、相对路径等所有东西都符合直觉，也便于用户手动复现同一条命令。
    Write-Host "正在执行：Beneflow.Api.exe --migrate（工作目录：$PublishDir）"
    Push-Location $PublishDir
    try {
        & ".\Beneflow.Api.exe" --migrate 2>&1 | Select-Object -Last 8
    } finally {
        Pop-Location
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Host "数据库初始化失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
        Write-Host "排查方向：" -ForegroundColor Yellow
        Write-Host "  1. 用 appsettings 里的连接串能连上 SQL Server 吗？"
        Write-Host "  2. 那个登录名有 CREATE DATABASE / CREATE TABLE 权限吗？"
        Write-Host "  3. 完整日志：$PublishDir\logs\"
        exit 1
    }
    Write-Host "数据库已就绪（迁移已应用 + 演示数据已播种）" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== 4. 停止并删除已存在的服务（如果有） ===" -ForegroundColor Cyan
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -eq "Running") {
        Write-Host "正在停止旧服务……" -ForegroundColor Yellow
        Stop-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 2
    }
    sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "sc delete 失败（退出码 $LASTEXITCODE）。服务可能已被标记为待删除；" -ForegroundColor Red
        Write-Host "等几秒后重新运行本脚本。" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "旧服务已删除" -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}

Write-Host ""
Write-Host "=== 5. 注册为 Windows 服务 ===" -ForegroundColor Cyan
$binPath = "`"$exe`""
sc.exe create $ServiceName binPath= $binPath start= auto | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "sc create 失败（退出码 $LASTEXITCODE）" -ForegroundColor Red
    exit 1
}

Write-Host "服务名：      $ServiceName"
Write-Host "描述：        Beneflow 进销存管理系统"
Write-Host "启动类型：    自动"
Write-Host "可执行文件：  $exe"

sc.exe description $ServiceName "Beneflow 进销存管理系统 - Kestrel HTTP 5000 / HTTPS 5001" | Out-Null

Write-Host ""
Write-Host "=== 6. 配置崩溃自动重启 ===" -ForegroundColor Cyan
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Write-Host "已配置：第 1 次崩溃 5 秒 / 第 2 次 10 秒 / 第 3 次 30 秒后自动重启，24 小时无故障则重置计数"

Write-Host ""
Write-Host "=== 7. 放行 5000/5001 的防火墙规则（所有配置文件） ===" -ForegroundColor Cyan
# 放在启动服务之前：万一服务启动失败，也可以先手动用 exe 前排查网络问题
foreach ($port in $Ports) {
    Get-NetFirewallRule -DisplayName "Beneflow.Api $port*" -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "Beneflow.Api TCP $port" -Direction Inbound `
        -LocalPort $port -Protocol TCP -Action Allow -Profile Any | Out-Null
    New-NetFirewallRule -DisplayName "Beneflow.Api UDP $port" -Direction Inbound `
        -LocalPort $port -Protocol UDP -Action Allow -Profile Any | Out-Null
    Write-Host "已放行 $port（TCP+UDP，所有配置文件）"
}

Write-Host ""
Write-Host "=== 8. 启动服务 ===" -ForegroundColor Cyan
try {
    Start-Service -Name $ServiceName
} catch {
    # 不在这里退出：下面的诊断信息比异常堆栈更有用
    Write-Host "Start-Service 报错：$($_.Exception.Message)" -ForegroundColor Red
}
Start-Sleep -Seconds 5
$svc = Get-Service -Name $ServiceName
Write-Host "服务状态：$($svc.Status)" -ForegroundColor Green

Write-Host ""
Write-Host "=== 9. 健康检查 ===" -ForegroundColor Cyan

$proc = Get-Process -Name "Beneflow.Api" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    Write-Host "进程在运行：PID=$($proc.Id)，启动时间=$($proc.StartTime)" -ForegroundColor Green
} else {
    Write-Host "进程没在运行，多半是启动失败。请查事件查看器 -> Windows 日志 -> 应用程序 -> 来源 'Beneflow.Api'" -ForegroundColor Red
    Write-Host "或者直接前台运行看报错：$exe" -ForegroundColor Yellow
}

# Use curl.exe instead of Invoke-WebRequest - PS5.x has a bug where ScriptBlock
# cert validation callback fails in async TLS thread ("no runspace available")
Write-Host "HTTP 5000 自检：" -NoNewline
$http = & curl.exe -s -o NUL -w "%{http_code} %{time_total}s" http://127.0.0.1:5000/ 2>&1
if ($http -match "^200") {
    Write-Host " 通过（$http）" -ForegroundColor Green
} else {
    Write-Host " 失败（$http）" -ForegroundColor Red
}

Write-Host "HTTPS 5001 自检：" -NoNewline
$https = & curl.exe -k -s -o NUL -w "%{http_code} %{time_total}s ssl_verify=%{ssl_verify_result}" https://127.0.0.1:5001/ 2>&1
if ($https -match "^200") {
    Write-Host " 通过（$https）" -ForegroundColor Green
} else {
    Write-Host " 失败（$https）" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== 部署完成 ===" -ForegroundColor Green
Write-Host "服务名：    $ServiceName"
Write-Host "可执行文件：$exe"
Write-Host "工作目录：  $PublishDir"
Write-Host ""
Write-Host "常用命令：" -ForegroundColor Cyan
Write-Host "  启动：  Start-Service $ServiceName"
Write-Host "  停止：  Stop-Service $ServiceName"
Write-Host "  重启：  Restart-Service $ServiceName"
Write-Host "  状态：  Get-Service $ServiceName"
Write-Host "  日志：  Get-WinEvent -LogName Application -ProviderName 'Beneflow.Api' -MaxEvents 50"
Write-Host "  卸载：  Stop-Service $ServiceName; sc.exe delete $ServiceName"
Write-Host ""
Write-Host "本机：    http://localhost:5000  /  https://localhost:5001"
Write-Host "局域网：  http://<局域网IP>:5000   /   https://<局域网IP>:5001"
Write-Host ""
Write-Host "初始账号：admin / cashier / buyer，密码 123456（正式使用前务必修改）" -ForegroundColor Yellow
