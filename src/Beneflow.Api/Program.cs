using System.Text;
using Beneflow.Api.Data;
using Beneflow.Api.Services;
using Beneflow.Api.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddScoped<ISupplierService, SupplierService>();
builder.Services.AddHttpClient();  // BarcodeService 调第三方 API 用
builder.Services.AddHostedService<BackupHostedService>();   // 每日 02:00 自动全量备份（含启动补备）

// ---------- JWT Bearer 认证 ----------
var jwt = builder.Configuration.GetSection("Jwt");
var keyBytes = Encoding.UTF8.GetBytes(jwt["Secret"]!);

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

// ---------- CORS（开发期放开，生产同域部署无需） ----------
builder.Services.AddCors(o => o.AddPolicy("Dev", p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

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
app.UseCors("Dev");

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
