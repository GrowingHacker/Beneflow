# Beneflow（百惠通）

便利店进销存管理系统。.NET 8 Web API + 原生 Vue 3 / Element Plus 前端（本地离线 lib，无 npm、无构建工具）。

## 预览

| | |
|---|---|
| ![首页看板](docs/images/dashboard.png) | ![收银台](docs/images/sales.png) |
| ![商品档案](docs/images/products.png) | ![财务报表](docs/images/reports.png) |

## 功能

- 收银开单 / 销售退货 / 赊账管理
- 采购入库 / 采购退货
- 库存盘点 / 库存预警 / 临期预警
- 商品条码：扫码录入；系统首页生成二维码，手机扫码后打开手机端入库页面（html5-qrcode，需 HTTPS 调用摄像头）
- 报表：利润分析 / 供应商对账 / 赊账汇总
- 用户 / 角色 / 菜单权限、操作日志
- JWT 认证；局域网 HTTPS（自动生成自签证书，手机端安装根 CA 后扫码即可访问移动页面）

## 快速开始

### 1. 环境要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server 2019+（Express 即可）
- EF 迁移工具（已装可跳过）：

```powershell
dotnet tool install --global dotnet-ef
```

### 2. 配置

复制配置模板并填入真实值（`appsettings.json` 含密钥，已被 .gitignore 排除）：

```powershell
copy src\Beneflow.Api\appsettings.example.json src\Beneflow.Api\appsettings.json
```

需要修改的项：

| 配置 | 说明 |
|---|---|
| `ConnectionStrings:Default` | SQL Server 连接串（账号、密码、库名） |
| `Jwt:Secret` | 随机字符串，至少 32 字节 |
| `Security:AesKey` | base64 的 32 字节随机密钥（用于敏感字段加密） |

### 3. 建库

```powershell
dotnet ef database update --project src\Beneflow.Api
```

### 4. 运行

```powershell
dotnet run --project src\Beneflow.Api
```

- 首次启动自动种子演示数据（商品、供应商、单据、系统配置）
- TLS 证书自动生成于 `%LocalAppData%\Beneflow\tls`，手机端安装根 CA 证书后 HTTPS 受信
- 数据库备份输出到 `backups/`（不入库）

## 演示账号 & 初始账号

| 用户名 | 密码 | 角色 |
|---|---|---|
| admin | 123456 | 店主（全部菜单） |
| cashier | 123456 | 收银员（收银/退货/赊账/库存） |
| buyer | 123456 | 采购员（商品/供应商/采购/库存） |

## 目录结构

```
Beneflow/
├── src/Beneflow.Api/      # 后端项目
│   ├── Controllers/       # API 控制器（一文件一控制器，统一继承 BaseApiController）
│   ├── Services/          # 业务服务（按域分目录：Users/Product/Sale/...，接口 + 实现）
│   ├── Models/            # 实体（Entities/）、DTO、统一响应 ApiResult
│   ├── Data/              # AppDbContext、DbSeeder 种子数据
│   ├── Migrations/        # EF Core 迁移
│   ├── Utils/             # PasswordHasher、AesStringCipher、NetUtil
│   └── wwwroot/           # 前端静态页（Vue 3 + Element Plus，动态加载 pages/*.html）
│       └── lib/           # 离线第三方库（随仓库提交，无需下载）
├── scripts/               # 部署/运维 PowerShell 脚本
│   ├── deploy-kestrel-service.ps1
│   ├── update-service.ps1
│   └── uninstall-service.ps1
├── docs/                  # 文档与截图
├── README.md
└── .gitignore
```

## 生产部署

支持两种部署模式，**推荐模式 A（Kestrel + Windows Service）**：

### 模式 A：Kestrel + Windows Service（推荐）

- 入口：`Beneflow.Api.exe`（apphost），由 SCM 启动
- 账户：`LocalSystem`
- TLS：Kestrel 进程内握手，证书由 `TlsCertService` 自动签发，**无需 netsh / IIS 证书绑定**
- 崩溃恢复：`sc.exe failure` 配 1/2/3 次崩 5s/10s/30s 后自动重启
- 开机自启：`start= auto`
- 证书路径：`C:\Windows\System32\config\systemprofile\AppData\Local\Beneflow\tls\`

### 模式 B：IIS In-Process（备用）

- 入口：IIS 站点 + `web.config` 的 `<aspNetCore>` 段
- TLS：HTTP.SYS 内核处理，需 `netsh http add sslcert` 绑定证书到 `LocalMachine\My`
- 依赖：必须装 .NET Core Hosting Bundle
- 优势：IIS 管理器可视化监控、应用池自动重启、静态文件性能最优
- 劣势：证书导入到 `LocalMachine\Root/My` 需 UAC 提权、与 Kestrel 端口冲突

### 一键部署脚本

所有脚本位于 `scripts/`，**必须在管理员 PowerShell 中运行**：

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
cd D:\Beneflow\scripts
```

| 脚本 | 用途 |
|------|------|
| [deploy-kestrel-service.ps1](scripts/deploy-kestrel-service.ps1) | 首次部署：注册服务 + 防火墙 + 健康检查 |
| [update-service.ps1](scripts/update-service.ps1) | 重新部署：停服务 → dotnet publish → 启服务 → 验证 |
| [uninstall-service.ps1](scripts/uninstall-service.ps1) | 卸载：删服务 + 防火墙 + 发布目录 + 证书 |

### 首次部署

```powershell
# 1. 发布（framework-dependent，服务器已装 .NET 8 Runtime）
dotnet publish D:\Beneflow\src\Beneflow.Api\Beneflow.Api.csproj `
    -c Release -r win-x64 --self-contained false `
    -o D:\Beneflow\publish

# 2. 配置生产环境
Copy-Item D:\Beneflow\src\Beneflow.Api\appsettings.example.json `
          D:\Beneflow\publish\appsettings.Production.json
# 编辑 D:\Beneflow\publish\appsettings.Production.json，填生产数据库连接串

# 3. 应用 EF 迁移（首次部署必做）
$env:ASPNETCORE_ENVIRONMENT="Production"
$env:ConnectionStrings__Default="<生产连接串>"
dotnet ef database update --project D:\Beneflow\src\Beneflow.Api `
    --connection "<生产连接串>"

# 4. 注册服务 + 防火墙 + 健康检查（一键）
Set-ExecutionPolicy -Scope Process Bypass -Force
D:\Beneflow\scripts\deploy-kestrel-service.ps1

# 5. 手机端装根 CA（见下文「证书管理」）
```

部署成功后访问：

- HTTP:  http://localhost:5000
- HTTPS: https://localhost:5001
- 局域网：http://<本机IP>:5000 / https://<本机IP>:5001

### 重新部署（代码更新后）

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
D:\Beneflow\scripts\update-service.ps1
```

脚本自动执行：停服务 → dotnet publish → 启服务 → curl 健康检查。

### 配置文件

`D:\Beneflow\publish\appsettings.Production.json`（自己创建，参考 `appsettings.example.json`）：

```json
{
  "ConnectionStrings": {
    "Default": "Server=生产服务器IP;Database=Beneflow;User Id=sa;Password=xxx;TrustServerCertificate=True"
  },
  "Serilog": {
    "MinimumLevel": "Information"
  },
  "Jwt": {
    "Issuer": "Beneflow",
    "Audience": "Beneflow",
    "Key": "<生产 JWT 密钥，至少 32 字符>"
  }
}
```

修改后重启服务生效：`Restart-Service Beneflow.Api`

### 证书管理

`TlsCertService` 启动时自动检测所有局域网 IP，签发包含 IP SAN 的叶子证书。局域网 IP 变化后重启服务即可，根 CA 不变（手机端无需重装）。

**手机端首次必装根 CA**：

1. iPhone Safari 访问 `http://<服务器IP>:5000/ca.crt` 下载根 CA
2. 设置 → 通用 → VPN 与设备管理 → 安装
3. 设置 → 通用 → 关于本机 → 证书信任设置 → 启用 `Beneflow Local Root CA`
4. 访问 `https://<服务器IP>:5001/` 应显示绿锁标志，无安全提示

Android 类似，下载后到「设置 → 安全 → 加密与凭据 → 安装证书 → CA 证书」。

### 防火墙

部署脚本自动添加规则（所有 Profile + TCP/UDP + 5000/5001）：

```powershell
Get-NetFirewallRule -DisplayName "Beneflow.Api*" |
    Format-Table DisplayName, Enabled, Direction, Action, Profile
```

### 常用运维命令

```powershell
# 服务管理
Start-Service Beneflow.Api
Stop-Service Beneflow.Api
Restart-Service Beneflow.Api
Get-Service Beneflow.Api

# 进程与端口
Get-Process -Name "Beneflow.Api" | Select-Object Id, StartTime
netstat -ano | findstr ":5000 :5001"

# 日志（Serilog 文件）
Get-Content "D:\Beneflow\publish\logs\Beneflow.log" -Tail 50 -Wait

# 事件查看器
Get-WinEvent -LogName Application -ProviderName "Beneflow.Api" -MaxEvents 30 |
    Format-Table TimeCreated, LevelDisplayName, Message -Wrap

# 证书目录
Get-ChildItem "C:\Windows\System32\config\systemprofile\AppData\Local\Beneflow\tls"
```

### 卸载

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
D:\Beneflow\scripts\uninstall-service.ps1
```

选项：`-KeepPublish` 保留发布目录；`-KeepCerts` 保留证书（手机端已装根 CA 时用）。

## 常见问题

### 手机访问 HTTPS 提示"不安全"，强行访问后不再提示

浏览器缓存了"例外决策"（IP+端口+证书指纹）。**这不是真安全**，应装根 CA：访问 `http://<IP>:5000/ca.crt` 下载并信任根 CA。

### 手机访问超时

最常见是 **Windows 防火墙未放行 5000/5001**，重跑 `deploy-kestrel-service.ps1` 或手动添加规则：

```powershell
New-NetFirewallRule -DisplayName "Beneflow.Api TCP 5000" -Direction Inbound `
    -LocalPort 5000 -Protocol TCP -Action Allow -Profile Any
```

若使用 iPhone 个人热点，默认开启 AP 隔离（客户端互不可见），需改用真正的 Wi-Fi 路由器或笔记本开 Windows 移动热点。

### 服务启动失败，HTTP 5000 不通

```powershell
Get-WinEvent -LogName Application -ProviderName "Beneflow.Api" -MaxEvents 30
```

最常见原因：
- `appsettings.Production.json` 数据库连接串无效
- EF 迁移未应用：`dotnet ef database update`
- 5000/5001 端口被 IIS 抢占：`Stop-Service W3SVC`

### 5001 端口被 HTTP.SYS 占用

```powershell
netstat -ano | findstr :5001
# 若 PID=4 (System)，说明 HTTP.SYS 在监听
netsh http delete sslcert ipport=0.0.0.0:5001  # 清除 IIS 拮留
Stop-Service W3SVC
Set-Service W3SVC -StartupType Manual
Restart-Service Beneflow.Api
```

### PowerShell 5.x Invoke-WebRequest HTTPS 自测失败

PS 5.x 的 `ServerCertificateValidationCallback = {$true}` 在 TLS 异步回调线程里找不到 runspace，导致握手中断。**与 Kestrel 无关**。用 `curl.exe -k` 代替：

```powershell
curl.exe -k -s -o NUL -w "%{http_code}`n" https://127.0.0.1:5001/
```

### 发布时下载 Runtime 包失败（ResponseEnded）

国内访问 nuget.org 大包易断流。改用 framework-dependent 模式：

```powershell
dotnet publish ... --self-contained false
```

或配置 NuGet 国内镜像：

```xml
<!-- NuGet.Config -->
<packageSources>
  <add key="tuna" value="https://nuget.tuna.tsinghua.edu.cn/v3/index.json" />
</packageSources>
```

### 中文 PowerShell 输出乱码

PS 5.x 默认 GBK，输出 UTF-8 中文乱码。修一下：

```powershell
chcp 65001
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
```

或装 PowerShell 7：`winget install Microsoft.PowerShell`，用 `pwsh` 启动。

## 安全提示

- 任何密钥（数据库密码、JWT Secret、AesKey）不要提交到仓库
- 生产部署务必修改演示账号密码，并更换 `appsettings.example.json` 中的占位密钥
