using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Beneflow.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 集成测试基类：用 WebApplicationFactory 在进程内启动真实 API 管线
/// （路由 → 认证 → 授权 → 控制器 → Service → InMemory 数据库），
/// 通过 HttpClient 真实发起 HTTP 请求，验证跨模块协作与统一响应契约。
///
/// 每个测试类共享一个 TestWebAppFactory 实例（IClassFixture），工厂为每个实例创建
/// 独立的 InMemory 数据库；Program 启动时会自动 seed 演示账号与基础数据，
/// 因此每个测试都能以 admin/cashier/buyer（密码 123456）等种子数据为基础。
/// </summary>
public abstract class IntegrationTestBase : IClassFixture<TestWebAppFactory>
{
    protected TestWebAppFactory Factory { get; }
    protected HttpClient Client { get; }

    // ASP.NET Core 默认以 camelCase 反序列化请求体，这里统一用 camelCase 序列化，
    // 否则 PostAsJsonAsync 默认的 PascalCase 会导致所有字段绑定为空。
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected IntegrationTestBase(TestWebAppFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }

    // ========== 认证辅助 ==========

    /// <summary>登录并自动为 Client 设置 Bearer Token，返回 token 字符串</summary>
    protected async Task<string> LoginAsAsync(string username, string password)
    {
        var resp = await PostJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.True(resp.IsSuccessStatusCode, $"登录请求应成功，实际 HTTP {resp.StatusCode}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var token = body.GetProperty("data").GetProperty("token").GetString()!;
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    /// <summary>以店主身份登录（种子账号 admin / 123456）</summary>
    protected Task<string> LoginAsAdminAsync() => LoginAsAsync("admin", "123456");

    // ========== 请求辅助（camelCase） ==========

    protected async Task<HttpResponseMessage> PostJsonAsync(string path, object body) =>
        await Client.PostAsync(path, new StringContent(JsonSerializer.Serialize(body, Camel), Encoding.UTF8, "application/json"));

    protected async Task<HttpResponseMessage> PutJsonAsync(string path, object body) =>
        await Client.PutAsync(path, new StringContent(JsonSerializer.Serialize(body, Camel), Encoding.UTF8, "application/json"));

    protected async Task<HttpResponseMessage> PostAsync(string path) =>
        await Client.PostAsync(path, null);

    // ========== 响应解析辅助 ==========

    /// <summary>读取 {code,message,data} 响应，返回整个 body</summary>
    protected static async Task<JsonElement> ReadBody(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>断言 HTTP 成功且 code==0，返回 data 节点</summary>
    protected static async Task<JsonElement> ExpectOk(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(resp.IsSuccessStatusCode,
            $"HTTP 应为 2xx，实际 {resp.StatusCode}，响应：{body.GetRawText()}");
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        return body.GetProperty("data");
    }
}

/// <summary>
/// 自定义工厂：把 AppDbContext 从真实 SQL Server 重定向到独立的 InMemory 数据库，
/// 并移除每日自动备份后台服务（测试无需落地备份文件，也避免其副作用）。
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"BeneflowInt_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // 移除 EF 默认注册的 DbContext（原指向真实 SQL Server）
            var optDesc = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (optDesc != null) services.Remove(optDesc);
            var ctxDesc = services.SingleOrDefault(d => d.ServiceType == typeof(AppDbContext));
            if (ctxDesc != null) services.Remove(ctxDesc);

            // InMemory 不支持事务：业务 Service 大量使用 BeginTransactionAsync，
            // InMemory 默认会把它当作异常抛出（TransactionIgnoredWarning）。
            // 忽略该警告后事务在 InMemory 中退化为 no-op（与生产 SQL Server 行为一致地提交），
            // 既不影响断言（最终一致性由 SaveChanges 直接落库保证），也不污染生产代码。
            services.AddDbContext<AppDbContext>(o => o
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            // 测试不需要每日备份后台服务（会访问磁盘/排程），移除避免副作用
            services.RemoveAll<IHostedService>();
        });
    }
}
