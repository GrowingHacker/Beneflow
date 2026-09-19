# Prepare the SQL Server instance that Beneflow.Api needs
# Usage: run in elevated PowerShell
#
#   ⚠️ 本脚本**不安装 SQL Server**。请先自行把 SQL Server 装好（Express 版即可），
#      本脚本只负责把它配置成「应用能用的样子」。
#      不接管安装的原因：静默安装那套参数（尤其 /SQLSYSADMINACCOUNTS 那个带空格的账号
#      列表）依赖命令行拼装，很难验证；而客户机上往往已经有现成实例，直接复用更省事。
#
#   本脚本负责「让数据库这一层对应用可用」，分两层：
#     A. 实例层（第 1~3 步）
#        1) 探测实例名（读注册表 Instance Names\SQL）+ 连接验证
#        2) 授权：把应用服务的身份（LocalSystem = NT AUTHORITY\SYSTEM）加进 sysadmin，
#           并把 BUILTIN\Administrators 也加进去（否则管理员连 SSMS 都进不去，无法救火）
#        3) 验证：报出「SQL 看到的你是谁」+「在不在 sysadmin」
#     B. 配置层（第 4~5 步）
#        4) 准备 publish\appsettings.Production.json（-SkipConfig 可跳过）
#        5) 把实例 / 账号 / 密码 / 连接字符串写进 scripts\数据库凭据.txt
#
#   为什么配置也归这个脚本：它是唯一知道「刚弄好的实例叫什么名字」的地方，连接串由它来写最自然。
#   但边界仍然清楚 —— 本脚本**不碰 publish 产物内容、不注册 Windows 服务**，那是 deploy 的事。
#
#   本脚本与 deploy-kestrel-service.ps1 共用 scripts\_common.ps1 里的 Write-ProductionConfig：
#   「以 appsettings.example.json 为底复制生成 → 补齐连接串与两个密钥 → 写完内容级校验」
#   这件事只有一份实现，两个脚本谁先跑都不会跑偏。
#
#   ⚠️ 不能让 Production 只写「连接串 + 两个密钥」这几项：Program.cs 里 Jwt:Issuer / Jwt:Audience
#      是直接读配置项且没有兜底值的，启动期又只校验 Jwt:Secret 与 Security:AesKey。
#      如果 Production 缺这两个键、appsettings.json（开发配置）又被删掉，应用照样启动、
#      也能登录成功，但签发的 token 过不了校验 —— 表现为「登录成功、之后每个请求都 401」。
#      以 example.json 为底整份带过去，才能保证键结构完整。
#
#   发行包（zip 解压后 publish\ 与 scripts\ 同级）：
#       <解压根目录>\
#       ├── publish\        已发布好的产物
#       └── scripts\        本脚本 + deploy-kestrel-service.ps1 + _common.ps1（三个必须同一目录）
#
#       cd <解压根目录>
#       .\scripts\setup-sqlserver.ps1
#
#   实例名默认**自动探测**：本机只有一个实例就直接用它；没有实例、或有多个无法替客户决定时，
#   会明确要求显式指定：
#       .\scripts\setup-sqlserver.ps1 -InstanceName SQLEXPRESS
#
#   ⚠️ 为什么必须显式授权：SQL Server 装完默认是「仅 Windows 身份验证」，sa 处于禁用状态
#      且没有任何密码；且自 SQL Server 2008 起 BUILTIN\Administrators 不再自动进 sysadmin。
#      而应用的 Windows 服务跑在 LocalSystem 下（deploy 脚本的 sc.exe create 没传 obj=），
#      不显式授权就会「库装好了、服务也起来了、一连库登录失败」。
#
#   ⚠️ 注意：deploy 脚本的 `--migrate` 是以「当前登录的管理员」身份跑的，
#      它成功**不代表服务身份能连**。真正决定服务能不能连库的是本脚本第 2 步的 sysadmin 授权。
#
# 流程：探测实例 → 授权 → 验证 → 写 appsettings.Production.json → 写凭据 txt → 摘要

param(
    [string]$InstanceName   = "",               # 留空 = 自动探测（本机唯一实例）；多个实例时必须显式指定
    [string]$SaPassword     = "",               # 留空 = 自动生成强密码并写入 -CredentialFile
    [string]$CredentialFile = "",               # 留空 = 本脚本同目录下的 数据库凭据.txt
    [string]$PublishDir     = "",               # 留空 = 脚本上一级目录下的 publish\（与 deploy 同一约定）
    [switch]$SkipMixedMode,                     # 不开混合模式（不用 sa，只走 Windows 身份验证）
    [switch]$SkipConfig,                        # 不写 publish\appsettings.Production.json
    [switch]$ForceConfig                        # 覆盖已存在的连接串 / 密钥（默认只填空缺与占位符）
)

$ErrorActionPreference = "Stop"

# 共用函数：JSON 路径读写 / 密钥生成 / 生产配置生成（见 scripts\_common.ps1）。
# 先判存在再 dot-source：少了它，报的会是「找不到路径」这种对客户毫无意义的错。
$commonPs1 = Join-Path $PSScriptRoot "_common.ps1"
if (-not (Test-Path $commonPs1)) {
    Write-Host "缺少必需文件：$commonPs1" -ForegroundColor Red
    Write-Host "它必须与本脚本放在同一目录（scripts\_common.ps1）。" -ForegroundColor Yellow
    Write-Host "请重新解压发行包 —— 此前解压出来的 scripts\ 目录不完整。" -ForegroundColor Yellow
    exit 1
}
. $commonPs1

Write-Host "=== 0. 环境预检 ===" -ForegroundColor Cyan

# 重启实例服务（让 LoginMode 生效）要求提权
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限。" -ForegroundColor Red
    Write-Host "请以管理员身份打开 PowerShell，然后重新运行本脚本。" -ForegroundColor Yellow
    exit 1
}
Write-Host "已以管理员身份运行" -ForegroundColor Green

# 用 PS 5.1 自带的 System.Data.SqlClient 连库：装 SQLEngine 时**不带 sqlcmd**，
# 而这个类不需要任何额外安装（.NET Framework 自带），默认也不加密，不会碰自签证书问题。
function Invoke-Sql {
    param(
        [string]$Database = "master",
        [string]$User     = "",
        [string]$Password = "",
        [Parameter(Mandatory = $true)][string]$Query
    )
    if ($User) {
        $cs = "Server=$server;Database=$Database;User ID=$User;Password=$Password;" +
              "Encrypt=False;TrustServerCertificate=True;Connect Timeout=10;Application Name=Beneflow.Setup"
    } else {
        $cs = "Server=$server;Database=$Database;Integrated Security=True;" +
              "Encrypt=False;TrustServerCertificate=True;Connect Timeout=10;Application Name=Beneflow.Setup"
    }
    $conn = New-Object System.Data.SqlClient.SqlConnection $cs
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText    = $Query
        $cmd.CommandTimeout = 120
        $da = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
        $dt = New-Object System.Data.DataTable
        [void]$da.Fill($dt)
        # ⚠️ 逗号不能省：`return $dt` 会让 PowerShell 把 DataTable **摊开成它的 DataRow** ——
        #    「查到 1 行」调用方拿到一个孤零零的 DataRow（没有 .Rows ⇒ 一索引就是
        #    「无法对 Null 数组进行索引」），「查到 0 行」调用方直接拿到 $null。
        #    `,$dt` 把整个 DataTable 当**一个**对象输出，调用方才能用 .Rows。
        return ,$dt
    } finally {
        $conn.Close()
    }
}

Write-Host ""
Write-Host "=== 1. 探测 SQL Server 实例 ===" -ForegroundColor Cyan

$regPath = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"

# 实例名不再由安装过程决定（本脚本不装 SQL Server）⇒ 默认自动探测：
#   显式给了 -InstanceName → 用它（但仍要核实本机确实有这个名字）
#   本机只有一个实例       → 用它
#   没有实例 / 有多个      → 报错退出，让客户显式指定（替他猜一个更危险）
$allInstances = @(Get-SqlInstanceNames)
if ($allInstances.Count -gt 0) {
    Write-Host "本机发现的实例：$($allInstances -join '、')"
} else {
    Write-Host "本机没有发现任何 SQL Server 实例。" -ForegroundColor Yellow
}

if ($InstanceName) {
    if ($allInstances -notcontains $InstanceName) {
        Write-Host "实例 '$InstanceName' 没有在本机注册。" -ForegroundColor Red
        Write-Host "核对真实实例名：  reg query `"$regPath`"" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "使用你指定的实例：$InstanceName" -ForegroundColor Green
} else {
    $auto = Resolve-SqlInstanceName -InstanceName ""
    if (-not $auto) {
        if ($allInstances.Count -eq 0) {
            Write-Host "请先安装 SQL Server（Express 版即可），然后重新运行本脚本。" -ForegroundColor Red
        } else {
            Write-Host "发现多个实例 —— 请显式告诉脚本用哪一个：" -ForegroundColor Red
            foreach ($name in $allInstances) {
                Write-Host "  .\scripts\setup-sqlserver.ps1 -InstanceName $name" -ForegroundColor Yellow
            }
        }
        exit 1
    }
    $InstanceName = $auto
    Write-Host "自动探测到本机唯一实例：$InstanceName" -ForegroundColor Green
}

# 连接串里的服务器写法：默认实例在注册表里叫 MSSQLSERVER，但**不能**连写成 ".\MSSQLSERVER"
$server = Get-SqlServerSpec -InstanceName $InstanceName
# 实例的 Windows 服务名：默认实例是 MSSQLSERVER，命名实例是 MSSQL$<实例名>
$instanceServiceName = if ($InstanceName -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { "MSSQL`$$InstanceName" }

# sa 密码：没给就自动生成（客户不可能自己想一个）。
# 必须在第 2 步**之前**生成 —— 那一节的「当前账号连不上时退回 sa」兜底路径要用它；
# 最终会连同连接串一起写进凭据 txt。
if (-not $SkipMixedMode -and -not $SaPassword) { $SaPassword = New-SaPassword }

Write-Host "服务器写法：$server"

Write-Host ""
Write-Host "=== 2. 授权需要连库的身份 ===" -ForegroundColor Cyan

# 先试着连上。实例是客户自己装的 ⇒ 装它的那个账号通常就是 sysadmin，当前提权身份应该连得上；
# 若我们不在 sysadmin 里（例如实例是别人装的、或换了个管理员账号），只能退回 sa。
$SqlFatal = $null
$connected = $false
try {
    Invoke-Sql -Query "SELECT 1" | Out-Null
    $connected = $true
    Write-Host "已用当前管理员账号连上。" -ForegroundColor Green
} catch {
    $SqlFatal = $_
}
if (-not $connected -and $SaPassword) {
    try {
        Invoke-Sql -User "sa" -Password $SaPassword -Query "SELECT 1" | Out-Null
        $connected = $true
        Write-Host "已用 'sa' 连上。" -ForegroundColor Green
    } catch {
        $SqlFatal = $_
    }
}
if (-not $connected) {
    Write-Host "用当前账号和 sa 都连不上 $server。" -ForegroundColor Red
    Write-Host "最后的错误：$($SqlFatal.Exception.Message)" -ForegroundColor Yellow
    Write-Host "排查方向：" -ForegroundColor Yellow
    Write-Host "  1. 实例在跑吗？   sc.exe query `"$instanceServiceName`""
    Write-Host "  2. 实例名对吗？  reg query `"$regPath`""
    Write-Host "  3. 应用的服务身份必须是 sysadmin —— 见本脚本头部说明。"
    exit 1
}

# 授权。CREATE LOGIN 要先判存在，否则重复执行会报错（脚本要能反复跑）。
# ALTER SERVER ROLE ... ADD MEMBER 是 SQL Server 2012+ 的写法。
$grantSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'NT AUTHORITY\SYSTEM')
    CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
ALTER SERVER ROLE sysadmin ADD MEMBER [NT AUTHORITY\SYSTEM];
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'BUILTIN\Administrators')
    CREATE LOGIN [BUILTIN\Administrators] FROM WINDOWS;
ALTER SERVER ROLE sysadmin ADD MEMBER [BUILTIN\Administrators];
"@
Invoke-Sql -Query $grantSql | Out-Null
Write-Host "已把 NT AUTHORITY\SYSTEM（应用服务身份）加入 sysadmin" -ForegroundColor Green
Write-Host "已把 BUILTIN\Administrators 加入 sysadmin（方便人用 SSMS 救火）" -ForegroundColor Green

# 混合模式：sa 默认是禁用的，这里启用并设密码，作为以后远程支持的应急通道。
# LoginMode 用 xp_instance_regwrite 而不是直接写注册表 —— 这个扩展存储过程会自己
# 解析到「当前实例对应的那段注册表路径」，手写 HKLM 路径在命名实例上很容易写错。
$loginModeChanged = $false
if (-not $SkipMixedMode) {
    $mixedSql = @"
ALTER LOGIN sa ENABLE;
ALTER LOGIN sa WITH PASSWORD = N'$SaPassword';
EXEC xp_instance_regwrite N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer',
     N'LoginMode', REG_DWORD, 2;
"@
    Invoke-Sql -Query $mixedSql | Out-Null
    $loginModeChanged = $true
    Write-Host "已开启混合模式，并启用 'sa'（密码为随机生成）" -ForegroundColor Green

    # LoginMode 是实例服务启动时读的，不重启不生效
    if (Get-Service -Name $instanceServiceName -ErrorAction SilentlyContinue) {
        Write-Host "正在重启 $instanceServiceName 让 LoginMode 生效……"
        Restart-Service -Name $instanceServiceName -Force
        Start-Sleep -Seconds 3
        Write-Host "服务状态：$((Get-Service -Name $instanceServiceName).Status)" -ForegroundColor Green
    } else {
        Write-Host "没找到服务 '$instanceServiceName' —— 请手动重启该实例。" -ForegroundColor Yellow
    }
} else {
    Write-Host "已跳过混合模式（-SkipMixedMode）：实例只允许 Windows 身份验证。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 3. 验证 ===" -ForegroundColor Cyan

$whoami = Invoke-Sql -Query "SELECT SUSER_SNAME() AS login_name, IS_SRVROLEMEMBER('sysadmin') AS is_sysadmin"
if ($whoami.Rows.Count -gt 0) {
    Write-Host "SQL 看到的调用者：$($whoami.Rows[0]['login_name'])  （sysadmin=$($whoami.Rows[0]['is_sysadmin'])）"
} else {
    # 这条查询没有 FROM，理论上必返 1 行；留着只是不让「查不到行」再变成一句晦涩的索引错误
    Write-Host "SQL 看到的调用者：（查询没有返回任何行）" -ForegroundColor Yellow
}

# 真正要验的是「应用服务那个身份」够不够权限 —— 这是个查表，不需要真的以 SYSTEM 身份连
$sysCheck = Invoke-Sql -Query @"
SELECT name, is_disabled, IS_SRVROLEMEMBER('sysadmin', name) AS is_sysadmin
FROM sys.server_principals
WHERE name IN (N'NT AUTHORITY\SYSTEM', N'BUILTIN\Administrators', N'sa');
"@
foreach ($row in $sysCheck.Rows) {
    Write-Host ("  {0,-26} 已禁用={1,-6} sysadmin={2}" -f $row['name'], $row['is_disabled'], $row['is_sysadmin'])
}

$info = Invoke-Sql -Query "SELECT SERVERPROPERTY('ProductVersion') AS v, SERVERPROPERTY('Collation') AS c"
if ($info.Rows.Count -gt 0) {
    Write-Host "版本：    $($info.Rows[0]['v'])"
    Write-Host "排序规则：$($info.Rows[0]['c'])"
}

$systemOk = @($sysCheck.Rows | Where-Object { $_['name'] -eq 'NT AUTHORITY\SYSTEM' -and $_['is_sysadmin'] -eq $true }).Count -gt 0
if (-not $systemOk) {
    Write-Host "NT AUTHORITY\SYSTEM 不是 sysadmin —— Windows 服务会连不上库。" -ForegroundColor Red
    exit 1
}
Write-Host "应用的服务身份（NT AUTHORITY\SYSTEM）可以连库。" -ForegroundColor Green

Write-Host ""
Write-Host "=== 4. 准备 publish\appsettings.Production.json ===" -ForegroundColor Cyan

# 连接串走 Windows 身份验证：第 2 步已把 LocalSystem（应用服务的身份）加成 sysadmin，
# 所以配置里**不需要出现任何密码** —— 客户也就不必知道 sa 是什么。
$winAuthCs = Get-BeneflowConnectionString -InstanceName $InstanceName

if ($SkipConfig) {
    Write-Host "已跳过（-SkipConfig）。请自行把下面这条写进 ConnectionStrings:Default：" -ForegroundColor Yellow
    Write-Host "  $winAuthCs"
} else {
    # 发布目录：与 deploy 同一套约定（脚本上一级目录下的 publish\）
    $pubDir = $PublishDir
    if (-not $pubDir) { $pubDir = Join-Path (Split-Path -Parent $PSScriptRoot) "publish" }

    if (-not (Test-Path $pubDir)) {
        Write-Host "没有找到 publish 目录：$pubDir" -ForegroundColor Yellow
        Write-Host "已跳过配置步骤。请把发布产物放过去再重跑，或者用 -PublishDir 指定位置。" -ForegroundColor Yellow
        Write-Host "  $winAuthCs"
    } else {
        # 写入与校验都在共用函数里 —— deploy 第 2 步调用的是同一个实现
        $cfgResult = Write-ProductionConfig -PublishDir $pubDir -InstanceName $InstanceName -Force:$ForceConfig
        if (-not $cfgResult.Ok) {
            Write-Host ""
            Write-Host "生产配置不可用：" -ForegroundColor Red
            foreach ($p in $cfgResult.Problems) { Write-Host "  - $p" -ForegroundColor Red }
            Write-Host "该文件的底本是 publish\appsettings.example.json，先把它补全再重跑。" -ForegroundColor Yellow
            exit 1
        }

        # appsettings.json 是**开发配置**（含 dev 连接串 / dev Jwt 密钥 / dev AesKey），
        # 会随发布产物一起被交出去。Production 已携带完整键结构，删掉它不会让配置缺键。
        if (Test-Path (Join-Path $pubDir "appsettings.json")) {
            Write-Host "注意：publish\appsettings.json 是**开发配置**，会随产物一起被交出去。" -ForegroundColor Yellow
            Write-Host "      Production 已携带完整键结构，所以交付前删掉它是安全的。" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
Write-Host "=== 5. 写凭据文件 ===" -ForegroundColor Cyan

# 应用走 Windows 身份验证 ⇒ 配置里不含密码。这个 txt 是给客户留档的：
# 实例名、「应用实际使用的连接字符串」、以及 sa 账号密码（应急通道）。
# 默认落在**本脚本同目录**（scripts\），这样 uninstall 删掉 publish\ 时它不会跟着没。
$sqlAuthCs = "Server=$server;Database=BeneflowDb;User ID=sa;Password=$SaPassword;Encrypt=False;" +
             "TrustServerCertificate=False;Pooling=False;MultipleActiveResultSets=False;Application Name=Beneflow.Api"

if (-not $CredentialFile) {
    $CredentialFile = Join-Path $PSScriptRoot "数据库凭据.txt"
}

$credLines = @(
    "Beneflow 数据库凭据 —— 请妥善保存，不要提交到代码仓库"
    "生成时间 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "实例     : $server"
    ""
    "应用使用的连接字符串（Windows 身份验证，配置里不含密码）:"
    $winAuthCs
    ""
    "数据库账号与密码（应急通道，日常用不到）:"
)
if ($SkipMixedMode) {
    $credLines += "  登录名 : （未启用：-SkipMixedMode，实例仅允许 Windows 身份验证）"
    $credLines += "  密码   : （无）"
} else {
    $credLines += "  登录名 : sa"
    $credLines += "  密码   : $SaPassword"
    $credLines += ""
    $credLines += "对应的连接字符串（SQL 身份验证）:"
    $credLines += $sqlAuthCs
}
$credLines += ""
$credLines += "说明: 应用服务以 LocalSystem 运行，已被加入 sysadmin，所以上面那条连接串不需要密码。"
$credLines += "      sa 只是应急通道；万一该授权被破坏，可用 sa 连接串救火。"

# 无 BOM 写入（与其它产物保持一致；txt 用 \r\n 换行更便于记事本查看）
[System.IO.File]::WriteAllLines($CredentialFile, $credLines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "凭据已写入：$CredentialFile" -ForegroundColor Yellow
Write-Host "  - 应用连接字符串（Windows 身份验证，无密码）"
if ($SkipMixedMode) {
    Write-Host "  - sa 账号密码：已跳过（-SkipMixedMode）"
} else {
    Write-Host "  - sa 账号与密码（应急通道）"
}

Write-Host ""
Write-Host "=== 数据库层已就绪 ===" -ForegroundColor Green
Write-Host "本脚本已完成：SQL Server 实例配置 + appsettings.Production.json + 凭据文件。"
Write-Host "本脚本**没有**做：BeneflowDb 这个库本身 —— 建表与演示数据是由"
Write-Host "'--migrate' 完成的，deploy-kestrel-service.ps1 第 3 步会调用它。"
Write-Host ""
Write-Host "下一步：" -ForegroundColor Cyan
Write-Host "  .\scripts\deploy-kestrel-service.ps1     # 建库建表（--migrate）+ 注册服务"
Write-Host ""
Write-Host "注意：deploy 的 --migrate 是以**你**（提权后的管理员）身份运行的，所以即使"
Write-Host "服务的身份连不上库，它也可能成功。这正是本脚本第 2 步存在的意义 ——"
Write-Host "那一步的授权，才是让 LocalSystem 服务真正能连上库的东西。"
if ($loginModeChanged) {
    Write-Host ""
    Write-Host "刚开启混合模式。如果实例服务重启失败，请重启机器。" -ForegroundColor Yellow
}
