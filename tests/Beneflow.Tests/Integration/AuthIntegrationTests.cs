using System.Net;
using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 认证与 HTTP 管线集成测试：验证统一响应契约、JWT 认证/授权、匿名与受保护端点的边界。
/// </summary>
public class AuthIntegrationTests : IntegrationTestBase
{
    public AuthIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokenAndUser()
    {
        var resp = await PostJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "123456" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadBody(resp);
        Assert.Equal(0, body.GetProperty("code").GetInt32());

        var data = body.GetProperty("data");
        Assert.False(string.IsNullOrEmpty(data.GetProperty("token").GetString()));
        Assert.Equal("admin", data.GetProperty("user").GetProperty("username").GetString());
        // 店主应拥有通配权限
        Assert.Contains("*", data.GetProperty("user").GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()).ToList());
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsFailCode()
    {
        var resp = await PostJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "wrong-password" });

        var body = await ReadBody(resp);
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("用户名或密码错误", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var resp = await Client.GetAsync("/api/v1/products");

        // 未携带 Token 时管线在授权阶段即返回 401（响应体为空，仅含 WWW-Authenticate 头），
        // 因此只断言状态码，不解析业务包络。
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithToken_Returns200()
    {
        await LoginAsAdminAsync();

        var resp = await Client.GetAsync("/api/v1/products");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadBody(resp);
        Assert.Equal(0, body.GetProperty("code").GetInt32());
    }
}
