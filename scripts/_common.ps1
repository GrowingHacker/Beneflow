# 共用工具函数（本文件没有主流程，只放函数定义）
#
#   由 setup-sqlserver.ps1 与 deploy-kestrel-service.ps1 共同 dot-source：
#       . (Join-Path $PSScriptRoot "_common.ps1")
#
#   ⚠️ 不要单独执行本文件。
#
#   这里集中两件两边都要做的事：
#     1) JSON 路径读写与占位符判定 —— PS 5.1 没有内置的「按路径改 JSON 某个键」
#     2) Write-ProductionConfig      —— 唯一一处「生产配置长什么样」的定义。
#        两个脚本共用同一份实现，避免各写一份、迟早跑偏。

# 生成随机密钥：Jwt:Secret 需要 ≥32 字节、Security:AesKey 需要 base64 的 32 字节。
# 统一输出 base64（纯 ASCII），放进 JSON 不会有转义问题。
function New-RandomBase64 {
    param([int]$Bytes)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $buf = New-Object byte[] $Bytes
    $rng.GetBytes($buf)
    return [Convert]::ToBase64String($buf)
}

# sa 密码：字符池刻意剔掉所有在 cmd / 连接串里有特殊含义的字符
# （& ^ % ! < > | " ' ` \ ; $ { } ( ) [ ] 空格）。这不只是洁癖：
# setup-sqlserver 的 -PrintCommandOnly 打出来的命令行是要给人粘进 cmd.exe 复核的，
# 里面只要有一个 & 就会被 cmd 当成命令分隔符。
function New-SaPassword {
    $pool = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789-_=+@#*?.'
    $rng  = [Security.Cryptography.RandomNumberGenerator]::Create()
    $buf  = New-Object byte[] 24
    $rng.GetBytes($buf)
    return (-join ($buf | ForEach-Object { $pool[$_ % $pool.Length] }))
}

# 判断一个配置值是不是「还没填」：null、空白，或**含** <...> 占位符。
# 注意是「含」而不是「等于」—— 占位符往往是嵌在整串中间的（连接串里的密码段、
# 实例名段都可能写成 <xxx>），只匹配整串会把它误判成「已填」而跳过补齐。
function Test-PlaceholderValue {
    param($Value)
    if ($null -eq $Value) { return $true }
    $s = "$Value".Trim()
    if ($s -eq "") { return $true }
    return ($s -match '<[^>]+>')
}

# 按 'A:B:C' 路径读 PSCustomObject 里的值；路径不存在返回 $null
function Get-JsonValue {
    param($Root, [string]$Path)
    $node = $Root
    foreach ($part in ($Path -split ':')) {
        if ($null -eq $node) { return $null }
        $prop = $node.PSObject.Properties[$part]
        if (-not $prop) { return $null }
        $node = $prop.Value
    }
    return $node
}

# 按 'A:B:C' 路径写值，中间层不存在就建出来（appsettings 的键都是这种冒号路径）
function Set-JsonValue {
    param($Root, [string]$Path, [string]$Value)
    $parts = $Path -split ':'
    $node  = $Root
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $name = $parts[$i]
        if (-not $node.PSObject.Properties[$name]) {
            $node | Add-Member -MemberType NoteProperty -Name $name -Value ([pscustomobject]@{})
        }
        $node = $node.PSObject.Properties[$name].Value
    }
    $leaf = $parts[-1]
    if ($node.PSObject.Properties[$leaf]) {
        $node.PSObject.Properties[$leaf].Value = $Value
    } else {
        $node | Add-Member -MemberType NoteProperty -Name $leaf -Value $Value
    }
}

# 本机已注册的 SQL Server 实例名（读注册表 Instance Names\SQL）。
# 脚本不再负责安装 SQL Server ⇒ 实例名由客户安装时自己决定，所以两个脚本都得能看出来。
function Get-SqlInstanceNames {
    $regPath = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"
    if (-not (Test-Path $regPath)) { return @() }
    return @(
        (Get-ItemProperty $regPath).PSObject.Properties |
            Where-Object { $_.Name -notlike 'PS*' } |
            ForEach-Object { $_.Name } |
            Sort-Object
    )
}

# 解析「该用哪个实例」：显式给了就用给的；没给且本机只有一个实例就用它；
# 其余情况（一个实例都没有 / 有多个无法替客户决定）返回 $null，由调用方决定怎么报错。
function Resolve-SqlInstanceName {
    param([string]$InstanceName)
    if ($InstanceName) { return $InstanceName }
    $all = @(Get-SqlInstanceNames)
    if ($all.Count -eq 1) { return $all[0] }
    return $null
}

# 连接串里服务器那一截。
# ⚠️ 默认实例在注册表里登记为 MSSQLSERVER，但**不能**连写成 ".\MSSQLSERVER"
#    （那是「名为 MSSQLSERVER 的命名实例」），默认实例要写 "."。
function Get-SqlServerSpec {
    param([string]$InstanceName)
    if (-not $InstanceName -or $InstanceName -eq 'MSSQLSERVER') { return '.' }
    return ".\$InstanceName"
}

# 生产连接串：Windows 身份验证，配置里**不出现任何密码**。
# 应用服务跑在 LocalSystem 下，setup-sqlserver 已把 NT AUTHORITY\SYSTEM 加进 sysadmin。
function Get-BeneflowConnectionString {
    param([string]$InstanceName)
    $srv = Get-SqlServerSpec -InstanceName $InstanceName
    return "Server=$srv;Database=BeneflowDb;Trusted_Connection=True;Encrypt=False;" +
           "TrustServerCertificate=False;Pooling=False;MultipleActiveResultSets=False;Application Name=Beneflow.Api"
}

# ---------------------------------------------------------------------------
# 连接串验证
# ---------------------------------------------------------------------------

# 打印连接串前把密码抹掉。SQL 身份验证的串里就明文躺着 sa 密码，
# 而这条串会被打进控制台、被客户截图、被远程协助看到 —— 不能整条原样吐出去。
function Get-MaskedConnectionString {
    param([string]$ConnectionString)
    return ($ConnectionString -replace '(?i)(password|pwd)\s*=\s*[^;]*', '$1=***')
}

# 真连一次 SQL Server，判断这条连接串能不能用。
#
# 为什么必须真连：候选连接串是脚本按「探测到的实例名」**拼**出来的，那是个猜测。
# 猜错了要到 deploy 第 3 步 --migrate 才会炸，而且炸出来是一堆 EF 堆栈，
# 客户根本看不出「其实就是实例名填错了」。提前验一次，报错才能说人话。
#
# 探测固定连 master：目标库 BeneflowDb 还没建时连它会直接报「无法打开数据库」，
# 而那是 --migrate 的正常前置状态，不能当成失败。
function Test-BeneflowConnectionString {
    param(
        [Parameter(Mandatory = $true)][string]$ConnectionString,
        [int]$TimeoutSeconds = 5
    )

    $r = [ordered]@{
        Ok               = $false
        Server           = ""
        Auth             = ""
        Integrated       = $false      # 是否 Windows 身份验证（决定「服务身份」那一关要不要查）
        LoginName        = ""
        IsSysadmin       = $false
        TargetDb         = ""
        TargetDbExists   = $false
        SystemAuthorized = $false      # Windows 身份下：服务的 LocalSystem 身份在不在 sysadmin
        Message          = ""
        Hint             = ""
    }

    try {
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString)
    } catch {
        $r.Message = "连接串格式不合法：$($_.Exception.Message)"
        $r.Hint    = 'Expected something like: Server=.\SQLEXPRESS;Database=BeneflowDb;Trusted_Connection=True;Encrypt=False'
        return [pscustomobject]$r
    }

    $r.Server     = "$($b.DataSource)"
    $r.Integrated = [bool]$b.IntegratedSecurity
    if ($r.Integrated) { $r.Auth = "Windows 身份验证（Trusted_Connection）" }
    else               { $r.Auth = "SQL 登录名 '$($b.UserID)'" }
    $r.TargetDb   = "$($b.InitialCatalog)"

    # 探测一定走 master，并换成短超时：服务器不通时不该让客户干等默认的 15 秒。
    #
    # ⚠️ 必须用**索引器 + 关键字名**，不能用属性名：SqlConnectionStringBuilder 实现了 IDictionary，
    #    PowerShell 的适配器会把 `$p.InitialCatalog = 'master'` 走到字典那条路上，然后报
    #    「不支持关键字: InitialCatalog」—— 属性**读得到、写不了**。实测只有
    #    `$p['Initial Catalog']` / `$p['Connect Timeout']` 这种写法生效。
    $probe = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString)
    $probe['Initial Catalog'] = "master"
    $probe['Connect Timeout'] = $TimeoutSeconds

    try {
        $cn  = New-Object System.Data.SqlClient.SqlConnection($probe.ConnectionString)
        $cn.Open()
        $cmd = $cn.CreateCommand()
        # 一条查询问齐四件事：我是谁 / 我是不是 sysadmin / 目标库在不在 / 服务的 LocalSystem 身份被授权了没。
        $cmd.CommandText = @'
SELECT
    SUSER_SNAME()                AS login_name,
    IS_SRVROLEMEMBER('sysadmin') AS is_sysadmin,
    DB_ID(@db)                   AS target_db_id,
    CASE WHEN EXISTS (
            SELECT 1 FROM sys.server_role_members rm
            JOIN sys.server_principals ro ON rm.role_principal_id  = ro.principal_id
            JOIN sys.server_principals me ON rm.member_principal_id = me.principal_id
            WHERE ro.name = 'sysadmin' AND me.name = 'NT AUTHORITY\SYSTEM')
         THEN 1 ELSE 0 END       AS system_sysadmin
'@
        [void]$cmd.Parameters.AddWithValue("@db", $r.TargetDb)
        $rd = $cmd.ExecuteReader()
        if ($rd.Read()) {
            $r.LoginName        = "$($rd['login_name'])"
            $r.IsSysadmin       = ($rd['is_sysadmin'] -ne [DBNull]::Value -and [int]$rd['is_sysadmin'] -eq 1)
            $r.TargetDbExists   = ($rd['target_db_id'] -ne [DBNull]::Value)
            $r.SystemAuthorized = ([int]$rd['system_sysadmin'] -eq 1)
        }
        $rd.Close()
        $cn.Close()
        $r.Ok      = $true
        $r.Message = "已连接"
    } catch {
        $r.Message = $_.Exception.Message
        # 下面几种是客户最可能撞上、又最难自己看出来的错，直接给出下一步。
        #
        # ⚠️ 正则必须**中英各写一份**：.NET 与 SqlClient 的异常消息是按当前系统语言本地化的，
        #    中文 Windows 上拿到的是「与网络相关的或特定于实例的错误」，只匹配 'network-related'
        #    会一条 Hint 都打不出来 —— 实测就是这么发现的（BAD server 用例没有 Hint）。
        #    `error: 26` 这种错误码是中英一致的，留着当兜底。
        if ($r.Message -match 'network-related|instance-specific|could not open a connection|网络相关|特定于实例|未找到或无法访问服务器|error: 26') {
            $r.Hint = "SQL Server 大概没在运行，或者 '$($r.Server)' 不是正确的实例名。"
        } elseif ($r.Message -match 'Login failed|登录失败') {
            $r.Hint = "服务器能连上，但这个登录名不被允许。请检查实例的身份验证模式。"
        } elseif ($r.Message -match 'certificate chain|证书链') {
            $r.Hint = "自签名证书问题：在连接串里加上 Encrypt=False。"
        } elseif ($r.Message -match 'timeout|超时|timed out') {
            $r.Hint = "服务器找到了但没响应。请确认实例在运行、且已为该实例启用 TCP/IP。"
        }
    }

    return [pscustomobject]$r
}

# 把连接串写回 publish\appsettings.Production.json 的 ConnectionStrings:Default。
# 单独抽出来，是为了让「用户手输的连接串」走**同一条**写入路径（-Depth 10 + 无 BOM），
# 而不是为了改一个键再抄一份 JSON 读写实现。
function Set-ProductionConnectionString {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDir,
        [Parameter(Mandatory = $true)][string]$ConnectionString
    )
    $prodPath = Join-Path $PublishDir "appsettings.Production.json"
    if (-not (Test-Path $prodPath)) { return $false }
    $cfg = (Get-Content -Raw -Encoding UTF8 $prodPath) | ConvertFrom-Json
    Set-JsonValue -Root $cfg -Path "ConnectionStrings:Default" -Value $ConnectionString
    $json = $cfg | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($prodPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    return $true
}

# 「连不上就问人」的重试循环：验当前连接串 → 通过就返回；不通过就请用户给一条新的 → 再验，
# 直到通过、或用户明确放弃。deploy 脚本第 2 步用它，所以「验证通过后方可继续」是**代码保证**的，
# 不是靠人自觉。
#
# -Reader 是为了能测：Read-Host 在无交互宿主（本机自测 / 计划任务）里会直接抛异常，
# 把读入做成可注入的，这条循环才有可能被真正跑一遍，而不是只能"看着觉得对"。
function Resolve-UsableConnectionString {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDir,
        [string]$ConnectionString = "",       # 命令行直接给了就用它，不再问
        [switch]$NonInteractive,              # 连不上就退出，绝不停下来等输入
        [scriptblock]$Reader = { param($Prompt) Read-Host $Prompt }
    )

    $prodPath = Join-Path $PublishDir "appsettings.Production.json"

    # 显式传进来的连接串：先写进配置再验 —— 与手工输入走**完全相同**的路径，不搞两条。
    if ($ConnectionString) {
        [void](Set-ProductionConnectionString -PublishDir $PublishDir -ConnectionString $ConnectionString)
        Write-Host "使用命令行传入的连接串。" -ForegroundColor Yellow
    }

    while ($true) {
        if (-not (Test-Path $prodPath)) {
            return [pscustomobject]@{
                Usable = $false; Skipped = $false
                Probe = [pscustomobject]@{
                    Ok = $false; Server = ""; Auth = ""; Integrated = $false; LoginName = ""
                    IsSysadmin = $false; TargetDb = ""; TargetDbExists = $false; SystemAuthorized = $false
                    Message = "没有找到 appsettings.Production.json"
                    Hint    = "配置没有准备好 —— 请从头重新运行本脚本。"
                }
            }
        }

        $cs = (Get-Content -Raw -Encoding UTF8 $prodPath | ConvertFrom-Json).ConnectionStrings.Default
        if (Test-PlaceholderValue $cs) {
            # 正常流程到不了这里（Write-ProductionConfig 一定会把占位符填掉），
            # 但手工把文件改回模板就会撞上 —— 给一句明确的话，好过拿这个假服务器名去连。
            $probe = [pscustomobject]@{
                Ok = $false; Server = ""; Auth = ""; Integrated = $false; LoginName = ""
                IsSysadmin = $false; TargetDb = ""; TargetDbExists = $false; SystemAuthorized = $false
                Message = "ConnectionStrings:Default 仍然是 <占位符>"
                Hint    = "请在下面输入一条真实的连接串。"
            }
        } else {
            Write-Host "正在测试：$(Get-MaskedConnectionString $cs)" -ForegroundColor Gray
            $probe = Test-BeneflowConnectionString -ConnectionString $cs
        }

        if ($probe.Server) { Write-Host ("  服务器 : {0}" -f $probe.Server) }
        if ($probe.Auth)   { Write-Host ("  身份   : {0}" -f $probe.Auth) }

        # ★ 这一关是「连得上」不够、还要「服务连得上」。
        #   deploy 的 sc.exe create 没传 obj=，服务跑在 LocalSystem 下；Windows 身份验证时
        #   它用的是 NT AUTHORITY\SYSTEM 这个登录名。当前管理员能连 ≠ 它能连 ——
        #   这正是「手测通过、装成服务就断」的经典陷阱，所以必须单独查一次。
        $blocked  = $false
        $blockMsg = ""
        if ($probe.Ok -and $probe.Integrated -and -not $probe.SystemAuthorized) {
            $blocked  = $true
            $blockMsg = "用的是 Windows 身份验证，但服务账号 NT AUTHORITY\SYSTEM " +
                        "在这个实例上没有授权。"
        }

        if ($probe.Ok -and -not $blocked) {
            $roleTag = if ($probe.IsSysadmin) { "  (sysadmin)" } else { "" }
            Write-Host ("  登录名 : {0}{1}" -f $probe.LoginName, $roleTag) -ForegroundColor Green
            if ($probe.TargetDbExists) {
                Write-Host ("  数据库 : {0}（已存在）" -f $probe.TargetDb) -ForegroundColor Green
            } else {
                Write-Host ("  数据库 : {0}（还没建 —— 第 3 步 --migrate 会建）" -f $probe.TargetDb) -ForegroundColor Yellow
            }
            Write-Host "连接通过" -ForegroundColor Green
            return [pscustomobject]@{ Usable = $true; Skipped = $false; Probe = $probe }
        }

        if ($blocked) {
            Write-Host "  失败   : $blockMsg" -ForegroundColor Red
            Write-Host "          服务会启动起来、然后死于「用户登录失败」。" -ForegroundColor Red
            Write-Host "  提示   : 跑 .\scripts\setup-sqlserver.ps1 去授权，" -ForegroundColor Yellow
            Write-Host "          或者输入一条服务能用的连接串（比如某个 SQL 登录名）。" -ForegroundColor Yellow
        } else {
            Write-Host "  失败   : $($probe.Message)" -ForegroundColor Red
            if ($probe.Hint) { Write-Host "  提示   : $($probe.Hint)" -ForegroundColor Yellow }
        }

        if ($NonInteractive) {
            Write-Host "非交互模式：不做提示。请改好连接串后重跑。" -ForegroundColor Red
            return [pscustomobject]@{ Usable = $false; Skipped = $false; Probe = $probe }
        }

        Write-Host ""
        Write-Host "请输入一条新的连接串，或者：" -ForegroundColor Cyan
        Write-Host "  <回车>          重试当前这条"
        Write-Host "  <实例名>        SQLEXPRESS  ->  Server=.\SQLEXPRESS;Database=BeneflowDb;Trusted_Connection=True;Encrypt=False"
        Write-Host "  .                使用默认实例"
        Write-Host "  s                跳过这项检查继续（服务大概率会失败）"
        Write-Host "  q                放弃（自己去改 publish\appsettings.Production.json）"
        $answer = "$(& $Reader '连接串：')".Trim()

        if ($answer -eq "") { continue }
        if ($answer -eq "q" -or $answer -eq "Q") {
            $probe.Message = "操作者已中止"
            return [pscustomobject]@{ Usable = $false; Skipped = $false; Probe = $probe }
        }
        if ($answer -eq "s" -or $answer -eq "S") {
            Write-Host "在连接串未经验证的情况下继续。" -ForegroundColor Yellow
            return [pscustomobject]@{ Usable = $true; Skipped = $true; Probe = $probe }
        }

        # 只给了实例名时按标准形状拼 —— 客户记不住连接串语法，但一定记得住实例名。
        if ($answer -notmatch '=') {
            $inst = $answer
            if ($inst -in @('.', '(local)', 'localhost', 'MSSQLSERVER')) { $inst = "" }
            $answer = Get-BeneflowConnectionString -InstanceName $inst
            Write-Host "已生成：$(Get-MaskedConnectionString $answer)" -ForegroundColor Gray
        }

        if (-not (Set-ProductionConnectionString -PublishDir $PublishDir -ConnectionString $answer)) {
            Write-Host "无法把连接串写进 appsettings.Production.json" -ForegroundColor Red
            return [pscustomobject]@{ Usable = $false; Skipped = $false; Probe = $probe }
        }
        Write-Host "已保存到 appsettings.Production.json" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# 生产配置
# ---------------------------------------------------------------------------

function Write-ProductionConfig {
    <#
    .SYNOPSIS
        准备 publish\appsettings.Production.json：不存在就由 appsettings.example.json 复制生成，
        再补齐「应用猜不出来」的键（连接串 + Jwt:Secret + Security:AesKey），最后做内容级校验。

    .DESCRIPTION
        为什么以 example.json 为底，而不是现写一份「最小配置」：
        Program.cs 里 Jwt:Issuer / Jwt:Audience 读的是配置项且**没有兜底值**，而启动期
        只校验 Jwt:Secret 与 Security:AesKey。如果 Production 里只有连接串和两个密钥、
        appsettings.json 又被删掉，应用照样能启动、也能登录成功，但签发的 token
        过不了 Issuer/Audience 校验 —— 表现为「登录成功、之后每个请求都 401」。
        example.json 带着完整键结构，复制它就不会漏。

        幂等：默认只填「缺失或仍是占位符」的键。-Force 才覆盖已填的值
        （注意 -Force 会换掉 Jwt:Secret，等于把所有人踢下线）。

    .OUTPUTS
        PSCustomObject: Ok / Path / Created / Filled[] / Kept[] / Problems[]
    #>
    param(
        [Parameter(Mandatory = $true)][string]$PublishDir,
        [string]$InstanceName = "",
        [switch]$Force
    )

    $prodPath = Join-Path $PublishDir "appsettings.Production.json"
    $exPath   = Join-Path $PublishDir "appsettings.example.json"

    $created = -not (Test-Path $prodPath)
    if ($created) {
        if (-not (Test-Path $exPath)) {
            return [pscustomobject]@{
                Ok = $false; Path = $prodPath; Created = $false
                Filled = @(); Kept = @()
                Problems = @("既没有 appsettings.Production.json，也没有模板 appsettings.example.json（目录：$PublishDir）")
            }
        }
        # Copy-Item 而不是 Move-Item：模板必须留在包里，重装或换机器还要用
        Copy-Item $exPath $prodPath
        Write-Host "已由 appsettings.example.json 复制生成 appsettings.Production.json" -ForegroundColor Yellow
    } else {
        Write-Host "appsettings.Production.json 已存在 —— 只补缺失的项"
    }

    try {
        $cfg = (Get-Content -Raw -Encoding UTF8 $prodPath) | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{
            Ok = $false; Path = $prodPath; Created = $created
            Filled = @(); Kept = @()
            Problems = @("$prodPath 不是合法 JSON：$($_.Exception.Message)")
        }
    }

    $filled = New-Object System.Collections.ArrayList
    $kept   = New-Object System.Collections.ArrayList

    $writes = @(
        @{ Path = "ConnectionStrings:Default"; Value = (Get-BeneflowConnectionString -InstanceName $InstanceName) }
        @{ Path = "Jwt:Secret";                Value = (New-RandomBase64 -Bytes 48) }
        @{ Path = "Security:AesKey";           Value = (New-RandomBase64 -Bytes 32) }
    )
    foreach ($w in $writes) {
        $cur = Get-JsonValue -Root $cfg -Path $w.Path
        if ((Test-PlaceholderValue $cur) -or $Force) {
            Set-JsonValue -Root $cfg -Path $w.Path -Value $w.Value
            [void]$filled.Add($w.Path)
            Write-Host ("  {0,-28} <- 已生成" -f $w.Path) -ForegroundColor Green
        } else {
            [void]$kept.Add($w.Path)
            Write-Host ("  {0,-28} <- 保留原值（已填过；-Force 才会覆盖）" -f $w.Path)
        }
    }

    # 连接串不是占位符、又没给 -Force 时，我们不会擅自改写它 —— 客户可能故意指向远程实例。
    # 但如果它和本机解析出的实例对不上，必须说一声：否则「装完连不上库」极难排查。
    $curCs = Get-JsonValue -Root $cfg -Path "ConnectionStrings:Default"
    if ($InstanceName -and -not $Force -and -not (Test-PlaceholderValue $curCs) -and ("$curCs" -notlike "*$InstanceName*")) {
        Write-Host "  注意：ConnectionStrings:Default 指向的实例不是 '$InstanceName'。" -ForegroundColor Yellow
        Write-Host "        已原样保留；要改写请传 -Force。" -ForegroundColor Yellow
    }

    # -Depth 10 不能省：这份配置有三层（如 Serilog:MinimumLevel:Override），
    # PS 5.1 的 ConvertTo-Json 默认 -Depth 2 会把嵌套对象压成字符串，配置直接写坏。
    $json = $cfg | ConvertTo-Json -Depth 10
    # 无 BOM：用 .NET 写，别用 Out-File -Encoding UTF8（那个会带 BOM）
    [System.IO.File]::WriteAllText($prodPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "已写入：$prodPath" -ForegroundColor Green

    # 写回后重新从磁盘读一遍再验 —— 只信磁盘上的内容，不信内存里的对象
    $problems = New-Object System.Collections.ArrayList
    $raw      = Get-Content -Raw -Encoding UTF8 $prodPath
    if ($raw -match '<[^>]+>') {
        [void]$problems.Add("文件里仍残留 <...> 占位符")
    }
    $verify = $raw | ConvertFrom-Json
    if (Test-PlaceholderValue (Get-JsonValue -Root $verify -Path "ConnectionStrings:Default")) {
        [void]$problems.Add("ConnectionStrings:Default 为空或仍是占位符")
    }
    $js = Get-JsonValue -Root $verify -Path "Jwt:Secret"
    if ([string]::IsNullOrWhiteSpace($js) -or [Text.Encoding]::UTF8.GetByteCount($js) -lt 32) {
        [void]$problems.Add("Jwt:Secret 缺失或不足 32 字节")
    }
    if ([string]::IsNullOrWhiteSpace((Get-JsonValue -Root $verify -Path "Security:AesKey"))) {
        [void]$problems.Add("Security:AesKey 缺失")
    }
    foreach ($k in @("Jwt:Issuer", "Jwt:Audience")) {
        if ([string]::IsNullOrWhiteSpace((Get-JsonValue -Root $verify -Path $k))) {
            [void]$problems.Add("$k 缺失（JWT 校验会全部失败，表现为登录后所有请求 401）")
        }
    }

    return [pscustomobject]@{
        Ok       = ($problems.Count -eq 0)
        Path     = $prodPath
        Created  = $created
        Filled   = $filled.ToArray()
        Kept     = $kept.ToArray()
        Problems = $problems.ToArray()
    }
}
