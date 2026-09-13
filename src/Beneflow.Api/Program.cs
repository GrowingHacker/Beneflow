using System.Text;
using Beneflow.Api.Data;
using Beneflow.Api.Services;
using Beneflow.Api.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------- Windows Service 托管 ----------
// 让 dotnet 进程可作为系统服务运行；以控制台直接启动时此调用不影响行为。
// 关键作用：
//   1. ContentRoot 默认设为程序所在目录（而非 %SystemRoot%\System32，避免 wwwroot 找不到）
//   2. 把 Console 输出重定向到 Windows 事件日志，便于服务崩溃时排查
//   3. 注册成 Windows Service 后，系统会以 LocalSystem 身份启动此进程
builder.Host.UseWindowsService(o =>
{
    // 出现在 Windows 服务管理器里的展示名
    o.ServiceName = "Beneflow.Api";
});

// ---------- 本地 HTTPS（为手机端实时扫码提供摄像头安全上下文） ----------
// 根 CA 缓存在 %LocalAppData%\Beneflow\tls；叶子证书 SAN 覆盖 localhost + 当前局域网 IPv4
var tls = new Beneflow.Api.Services.TlsCertService();
var lanIps = NetUtil.LanIPv4s();
if (lanIps.Count > 0)
    tls.Setup(lanIps, Environment.MachineName);
builder.Services.AddSingleton<ITlsCertService>(tls);

builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(5000); // HTTP
    if (tls.Ready)
        o.ListenAnyIP(5001, l => l.UseHttps(new Microsoft.AspNetCore.Server.Kestrel.Https.HttpsConnectionAdapterOptions
        {
            ServerCertificate = tls.ServerCert
        }));
});

// ---------- Serilog（按天滚动文件日志） ----------
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(builder.Environment.ContentRootPath, "logs", "log-.txt"),
        rollingInterval: RollingInterval.Day,
        shared: true)
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddControllers();

// ---------- 模型校验失败也走统一响应包络 ----------
// [ApiController] 默认返回 RFC7807 的 ValidationProblemDetails（只有 title/errors，没有 message），
// 与前端拦截器「读 err.response.data.message 弹提示」的约定不一致，会被降级成笼统的「网络错误」。
// 这里把它改写成和业务失败一致的 { code, message, data }，同时保留 HTTP 400 的语义。
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(o =>
{
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var msg = ctx.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? "请求参数不合法";

        return new Microsoft.AspNetCore.Mvc.ObjectResult(
            new { code = 400, message = msg, data = (object?)null })
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
    };
});

// ---------- OpenAPI / Swagger：仅开发环境暴露 ----------
// 生产是同域部署且接口全部需要鉴权，不对外提供接口文档，避免暴露内部接口结构。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "百惠通 Beneflow API",
            Version = "v1",
            Description = "便利店进销存系统接口。统一响应包络 { code, message, data }，code == 0 表示成功。"
                        + "先调用 POST /api/v1/auth/login 取 token，再点右上角 Authorize 填入（无需 Bearer 前缀）。",
        });

        // 把编译生成的 XML 注释文件接进 Swagger：控制器/模型上的 /// 说明会显示在接口文档里。
        // includeControllerXmlComments 让控制器级别的 <summary> 作为接口分组描述出现。
        var xmlPath = Path.Combine(AppContext.BaseDirectory,
            $"{typeof(Program).Assembly.GetName().Name}.xml");
        if (File.Exists(xmlPath))
        {
            o.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
        }

        o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "填入登录接口返回的 token（不需要写 Bearer 前缀）",
        });

        o.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer",
                    },
                },
                Array.Empty<string>()
            },
        });
    });
}

// ---------- EF Core + SQL Server（Code First） ----------
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

// ---------- 业务服务 ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AccountStatusCache>();   // 请求级账号状态校验缓存
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IPurchaseService, PurchaseService>();
builder.Services.AddScoped<ISaleService, SaleService>();
builder.Services.AddScoped<IStockService, StockService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddSingleton<AesStringCipher>();   // AES 加解密：敏感配置（API Key）落库加密
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IMenuService, MenuService>();
builder.Services.AddScoped<ILogService, LogService>();
builder.Services.AddScoped<ISettingService, SettingService>();
builder.Services.AddScoped<IBarcodeService, BarcodeService>();
builder.Services.AddScoped<IExcelExportService, ExcelExportService>();   // ClosedXML 美观 Excel 导出
builder.Services.AddScoped<ISupplierService, SupplierService>();
// 库存变更互斥锁：把「读库存-校验-扣减-提交」串行化，消除并发丢更新（详见类注释里的适用边界）
builder.Services.AddSingleton<StockMutationLock>();
builder.Services.AddHttpClient();  // BarcodeService 调第三方 API 用
builder.Services.AddHostedService<BackupHostedService>();   // 每日 02:00 自动全量备份（含启动补备）

// ---------- JWT Bearer 认证 ----------
// 启动期校验密钥：缺失或过短直接失败，避免运行期才抛空引用/签名异常。
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
    throw new InvalidOperationException(
        "配置项 Jwt:Secret 缺失或长度不足 32 字节。请复制 appsettings.example.json 为 appsettings.json 并填入随机密钥。");
if (string.IsNullOrWhiteSpace(builder.Configuration["Security:AesKey"]))
    throw new InvalidOperationException(
        "配置项 Security:AesKey 缺失。请复制 appsettings.example.json 为 appsettings.json 并填入 base64 的 32 字节随机密钥。");

var jwt = builder.Configuration.GetSection("Jwt");
var keyBytes = Encoding.UTF8.GetBytes(jwtSecret);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

// ---------- CORS：仅开发环境放开 ----------
// 生产是同域部署（前端静态文件由同一个 Kestrel 托管），根本不需要跨域，
// 因此不注册任何 CORS 策略，避免 AllowAnyOrigin 在生产被滥用。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(o => o.AddPolicy("Dev", p =>
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
}

var app = builder.Build();

// ---------- 全局异常兜底：未捕获异常也返回统一 { code, message } 包络 ----------
app.UseExceptionHandler(a => a.Run(async ctx =>
{
    ctx.Response.StatusCode = 500;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    if (ex != null) Log.Error(ex, "未处理异常：{Path}", ctx.Request.Path);
    await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(
        new { code = 500, message = "服务器内部错误，请稍后重试", data = (object?)null }));
}));

// ---------- 首次运行种子数据（幂等：已有用户则跳过） ----------
using (var scope = app.Services.CreateScope())
{
    try
    {
        await DbSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
    catch (Exception ex)
    {
        Log.Error(ex, "种子数据初始化失败（请确认数据库可连接且已执行 dotnet ef database update）");
    }
}

app.UseSerilogRequestLogging();
if (app.Environment.IsDevelopment())
{
    app.UseCors("Dev");

    // 必须放在 MapFallback 之前：Swagger UI 是中间件而非静态文件，
    // 否则 /swagger/index.html 会被 SPA 兜底路由吞掉。
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "Beneflow API v1"));
}

// 静态文件托管 wwwroot（前端主壳、页面片段、lib 全部在这里）
// HTML 文件禁用缓存（no-cache = 每次协商 revalidate），避免发布新版本后浏览器用旧壳加载新页面片段导致组件缺失；
// lib/css/js 仍走默认缓存策略。
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path;
        if (path.HasValue && path.Value.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
        }
    }
});

app.UseAuthentication();

// ---------- 账号状态实时校验：账号被禁用/删除后，已签发的 JWT 立即失效 ----------
// JWT 本身无状态（默认 12h 有效），这里在认证之后统一查一次库（30s 缓存 + 启停时主动失效），
// 被禁用账号的请求直接返回 401，前端拦截器会清 token 并跳登录页。
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true &&
        int.TryParse(ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid))
    {
        var cache = ctx.RequestServices.GetRequiredService<AccountStatusCache>();
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        if (!await cache.IsActiveAsync(db, uid))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { code = 401, message = "账号已被禁用或已删除", data = (object?)null });
            return;
        }
    }
    await next();
});

app.UseAuthorization();

app.MapControllers()
    // 端点级兜底：所有 Controller 端点要求登录认证。
    // 公开接口（例如登录）在方法上打 [AllowAnonymous] 即可豁免。
    .RequireAuthorization();

// ---------- 根 CA 证书下载（匿名）：手机安装信任后 https 局域网直连可开摄像头 ----------
app.MapGet("/ca.crt", (HttpContext ctx) =>
{
    var svc = ctx.RequestServices.GetRequiredService<ITlsCertService>();
    if (svc.CaCert == null) return Results.NotFound();
    var bytes = svc.CaCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
    return Results.File(bytes, "application/x-x509-ca-cert", "BeneflowRootCA.cer");
});

// SPA 兜底：非文件、非 API 路由回落到 index.html
// 外壳永远允许匿名访问；前端自己检查 localStorage 的 token 并在缺失时跳 login.html。
app.MapFallback("{*path:nonfile}", async ctx =>
{
    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(index))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(index);
    }
}).AllowAnonymous();

try
{
    Log.Information("百惠通 API 启动中...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "主机因异常终止");
}
finally
{
    Log.CloseAndFlush();
}
