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
- 进货单支持 Excel 批量导入：一个 Sheet = 一张进货单，先预览校验再批量建单
- 库存盘点 / 库存预警 / 临期预警
- 商品条码：扫码录入；系统首页生成二维码，手机扫码后打开手机端入库页面（html5-qrcode，需 HTTPS 调用摄像头）
- 报表：利润分析 / 供应商对账 / 赊账汇总
- 列表/报表导出：Excel（ClosedXML，标题合并 + 表头配色 + 合计行 + 自适应列宽）/ CSV（UTF-8 BOM）；按当前筛选条件导出全量
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
- **开发环境专属**：接口文档 Swagger UI 位于 http://localhost:5000/swagger ，先调 `POST /api/v1/auth/login` 拿 token，再点 Authorize 填入即可调试受保护接口。生产环境不注册该中间件。

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
│   │   └── Configurations/  # EF 实体映射（一实体一文件，IEntityTypeConfiguration）
│   ├── Migrations/        # EF Core 迁移
│   ├── Utils/             # PasswordHasher、AesStringCipher、NetUtil
│   └── wwwroot/           # 前端静态页（Vue 3 + Element Plus，动态加载 pages/*.html）
│       └── lib/           # 离线第三方库（随仓库提交，无需下载）
|
├── tests/Beneflow.Tests/  # 测试，包括单元测试和集成测试
|
├── scripts/               # 部署/运维 PowerShell 脚本
│   ├── deploy-kestrel-service.ps1
│   ├── update-service.ps1
│   └── uninstall-service.ps1
├── docs/                  # 界面截图（docs/images/，被 README 预览章节引用）
├── README.md
└── .gitignore
```

## 开发约定

- **分层**：Controller → Service（接口 + 实现）→ AppDbContext。控制器保持极薄，只做参数绑定与转发，业务逻辑一律放 Service。
- **实体映射**：schema 配置（表名、列长度、精度、索引、外键删除行为）统一写在 `Data/Configurations/` 下「一实体一文件」的 `IEntityTypeConfiguration<T>` 里，由 `AppDbContext.OnModelCreating` 通过 `ApplyConfigurationsFromAssembly` 装配。新增实体时请同步新增对应配置文件（即使无需配置也要建），以保持一一对应、避免漏配无人察觉。
- **统一响应**：`ApiResult` / `ApiResult<T>`，形状 `{ code, message, data }`，`code == 0` 为成功；分页用 `PagedResult<T>`。
- **路由**：`/api/v1/<资源复数>`。
- **认证**：控制器继承 `BaseApiController`（默认 `[Authorize]`），公开接口打 `[AllowAnonymous]`。
- **业务服务**：`Services/<域>/` 下 `IXxxService.cs` + `XxxService.cs`。单个 Service 超过约 400 行时，用 `partial class` 按职责拆成多个文件（如 `SaleService.cs` 核心单据流程、`SaleService.Return.cs` 退货、`SaleService.Credit.cs` 赊账、`SaleService.Export.cs` 导出）。拆文件只做物理切分、不改变依赖关系，因此不引入行为变更，可以随时用测试回归。
- **库存变更**：所有库存读写必须包在 `StockMutationLock` 的临界区内，详见「并发与一致性」一节。
- **校验职责**：请求 DTO 上的数据注解只管「字段形态」（必填、长度上限、ID 为正数），与数据库列定义一一对应，不通过直接返回 400；业务规则（库存够不够、是否允许赊账、数量是否必须为整数）一律留在 Service，因为需要查库或读系统设置。同一条规则不要在两处重复表达。
- **错误状态码**：业务失败沿用既有约定，返回 HTTP 200 + `code != 0`；参数校验失败返回 HTTP 400，但响应体仍是同样的 `{ code, message, data }` 包络，前端拦截器用同一套逻辑弹提示。
- **迁移**：`dotnet ef migrations add <PascalCase英文描述>`，不要手改快照。

## 并发与一致性

库存是这套系统里唯一会被多方同时改动的数据，因此单独说明。

### 问题

销售 / 进货 / 退货 / 盘点确认都会执行同一套动作：

```
读 StockQuantity  →  判断够不够  →  写回新值
```

在 SQL Server 默认的 ReadCommitted 隔离级别下，`SELECT` 不持有排他锁。两个并发收银可以读到**同一个旧值**、**同时通过校验**、再各自写回——后写的那次覆盖先写的（经典 lost update）。后果是同一件库存被卖出多次、库存被扣成负数。

同类问题还有两个：

- **单号撞车**：单号是 `{前缀}{yyyyMMdd}{当日序号}`，序号由「今日单据数 + 1」推导。并发建单会算出同一个序号，撞 `OrderNo` 唯一索引后抛异常返回 500。
- **重复处理**：作废 / 盘点确认都是「先查状态、再改状态」，并发下两次请求可以都通过检查，导致库存被重复回补或重复调整。

### 方案

`StockMutationLock`（`Services/Stock/StockMutationLock.cs`）提供两把锁：

| 锁 | 粒度 | 作用 |
|---|---|---|
| `AcquireOrderNoAsync()` | 全局互斥 | 覆盖「算号 → 落库提交」整段。序号来自已提交的行数，只在算号瞬间加锁不够，必须整段串行 |
| `AcquireAsync(productIds)` | 按商品 ID 分 64 桶 | 同一商品串行，不同商品并行 |

**加锁顺序全局统一：先单据号锁，再商品库存锁。** 顺序反了会与建单请求构成死锁环。多商品时内部按桶序号升序获取、逆序释放，因此两个请求即使以相反的入参顺序申请同一组商品也不会死锁。

临界区覆盖范围是「读-校验-改-提交」全程，且状态判定（是否已作废 / 是否已确认）保留在锁内重新读取，因此重复作废、重复确认的并发也会被串行化。

### 适用边界（重要）

该方案依赖**单进程部署**。本项目生产形态是 Kestrel + Windows Service 单实例，满足前提。

若将来改为多实例 / 多进程部署，进程内锁会失效，届时必须改为数据库层方案：

- `<c>Product</c>` 加 `RowVersion` 乐观并发，或
- 把扣减改为条件更新 `WHERE StockQuantity >= qty` 并校验受影响行数（`ExecuteUpdateAsync`）

对应地，`tests/Beneflow.Tests/Integration/StockConcurrencyIntegrationTests.cs` 需要改为针对真实 SQL Server 运行——InMemory 不支持事务，覆盖不到回滚语义。

### 测试怎么验证

- `StockMutationLockTests`：用临界区并发计数、交叉加锁超时上界等**不依赖墙钟时间**的方式验证锁语义（不去「睡一会儿看结果」，那种写法在全量并行跑测试时会假失败）。
- `StockConcurrencyIntegrationTests`：并发收银同一件库存，断言「成功单数 == 库存件数、库存不为负」这条端到端不变量。

## 测试

**当前状态：330 个用例全部通过（约 40 秒）。**

```powershell
dotnet test tests/Beneflow.Tests/Beneflow.Tests.csproj
```

测试分两层：

| 层次 | 说明 |
|---|---|
| 服务单元测试 | xUnit + EF Core InMemory，每个用例独立数据库，互不影响；覆盖各 Service 的业务分支与边界 |
| 集成测试 | `WebApplicationFactory<Program>` 在进程内启动真实 API 管线（认证 → 路由 → 控制器 → Service → 数据库），通过 `HttpClient` 真实发起 HTTP 请求，验证跨模块协作与统一响应契约 |
| 并发一致性测试 | 锁契约测试（确定性同步原语）+ 并发收银不超卖（端到端不变量），见「并发与一致性」一节 |

规模：后端源码约 7.7k 行，测试代码约 5.8k 行（比例约 0.75 : 1）。

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
    "Secret": "<生产 JWT 密钥，至少 32 字符>",
    "Issuer": "Beneflow",
    "Audience": "BeneflowClient",
    "ExpireMinutes": 720
  }
}
```

> 注意字段名是 **`Jwt:Secret`**（不是 `Key`），与 `appsettings.example.json` 及 `Program.cs` 保持一致。

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
