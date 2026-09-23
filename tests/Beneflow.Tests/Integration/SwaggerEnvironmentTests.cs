using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// Swagger 的环境隔离测试。
///
/// 注意：`WebApplicationFactory` 默认把环境设为 **Development**，
/// 所以既有的集成测试全都跑在 Development 下。要验证「生产不暴露接口文档」，
/// 必须显式切到 Production——否则测试会因为环境不对而失去意义（也会误判为通过）。
/// </summary>
public class SwaggerEnvironmentTests : IClassFixture<ProductionWebAppFactory>,
    IClassFixture<DevelopmentWebAppFactory>
{
    private readonly ProductionWebAppFactory _prod;
    private readonly DevelopmentWebAppFactory _dev;

    public SwaggerEnvironmentTests(ProductionWebAppFactory prod, DevelopmentWebAppFactory dev)
    {
        _prod = prod;
        _dev = dev;
    }

    [Fact]
    public async Task 生产环境_访问Swagger文档应返回404()
    {
        var client = _prod.CreateClient();

        var resp = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task 开发环境_应能获取Swagger文档()
    {
        var client = _dev.CreateClient();

        var resp = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.True(resp.IsSuccessStatusCode, $"Swagger 文档应可访问，实际 HTTP {resp.StatusCode}");
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("百惠通", json);
    }

    [Fact]
    public async Task 开发环境_应能打开Swagger界面()
    {
        var client = _dev.CreateClient();

        // index.html 带扩展名，会被 {*path:nonfile} 兜底路由排除，
        // 因此必须靠 SwaggerUI 中间件（注册在 MapFallback 之前）来处理
        var resp = await client.GetAsync("/swagger/index.html");

        Assert.True(resp.IsSuccessStatusCode, $"Swagger UI 应可访问，实际 HTTP {resp.StatusCode}");
    }

    /// <summary>
    /// 文档内容本身的两条硬约定。升级 Swashbuckle（6.6.2 → 10.2.3 这类跨大版本）时，
    /// 上面两条只会回答「页面还打得开吗」，回答不了「文档还是不是原来那份」——
    /// 安全定义与包络说明都可能静默丢/变样，所以在这里钉住。
    /// </summary>
    [Fact]
    public async Task 开发环境_Swagger文档_带Bearer定义且包络data有说明()
    {
        var client = _dev.CreateClient();
        var resp = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.True(resp.IsSuccessStatusCode, $"Swagger 文档应可访问，实际 HTTP {resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // ① Authorize 按钮靠它：securitySchemes 丢了，右上角填的 token 不会挂到请求上，整页调试全是 401
        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());

        // ② 路径按路由模板生成（升级不该把路径形状改掉）；顺带钉住新增的重置密码接口
        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/auth/login", out _), "登录接口应出现在文档里");
        Assert.True(paths.TryGetProperty("/api/v1/users/{id}/reset-password", out _),
            "重置密码接口应出现在文档里");

        // ③ 非泛型包络的 data 要带说明：读文档的人不会把空对象当成「有个空结构的字段」。
        //    这条同时证明 XML 注释真的被 Swashbuckle 读进了 schema（注释文件没生成/没接上时它会红）。
        var dataSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("ApiResult").GetProperty("properties").GetProperty("data");
        Assert.Contains("约定不带数据", dataSchema.GetProperty("description").GetString());
    }
}

/// <summary>把环境切到 Development 的测试工厂，用于覆盖只在开发环境注册的分支。</summary>
public class DevelopmentWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        base.ConfigureWebHost(builder);
    }
}

/// <summary>把环境切到 Production 的测试工厂，用于验证生产环境不会多开能力。</summary>
public class ProductionWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        base.ConfigureWebHost(builder);
    }
}
