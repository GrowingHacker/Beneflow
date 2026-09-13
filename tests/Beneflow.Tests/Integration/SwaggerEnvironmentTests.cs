using System.Net;
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
