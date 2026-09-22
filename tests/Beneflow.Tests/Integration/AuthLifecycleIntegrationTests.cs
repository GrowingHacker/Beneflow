using System.Net;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 认证与条码反查端点的上线前集成测试。
///
/// JWT 是无状态的，<c>logout</c> 在服务端只写一条操作日志 —— 「退出登录到底做了什么」
/// 只有经真实 HTTP 才看得全（顺带验证日志真的落了库）。条码反查这里只守控制器上的入参校验：
/// 真实联网反查依赖第三方接口，不能进测试（会慢、会因网络抖动假失败）。
/// </summary>
public class AuthLifecycleIntegrationTests : IntegrationSeedBase
{
    public AuthLifecycleIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task 退出登录_返回成功并落一条操作日志()
    {
        await LoginAsAdminAsync();

        var resp = await PostAsync("/api/v1/auth/logout");
        Assert.Equal(0, (await ReadBody(resp)).GetProperty("code").GetInt32());

        // JWT 无状态：退出登录不会让 token 失效，只在服务端留一条痕
        var logs = (await ReadBody(await Client.GetAsync("/api/v1/logs?keyword=退出登录"))).GetProperty("data");
        Assert.True(logs.GetProperty("total").GetInt32() >= 1, "退出登录应写一条操作日志");
        Assert.Equal("认证管理", logs.GetProperty("list").EnumerateArray().First().GetProperty("module").GetString());
    }

    [Fact]
    public async Task 未登录访问业务端点_返回401且响应体为空()
    {
        // 401 是空响应体（只有 WWW-Authenticate 头），前端拦截器靠状态码判定。
        // 断言「没有 body」是为了防止以后有人往 401 上挂 JSON 包络、把前端逻辑带偏。
        var resp = await Client.GetAsync("/api/v1/products");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(await resp.Content.ReadAsByteArrayAsync());
    }

    // ================= 条码反查 =================

    [Fact]
    public async Task 条码反查_长度不足时给中文业务失败()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await Client.GetAsync("/api/v1/barcode/lookup/123"));

        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("条码长度不足", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 条码反查_未登录返回401()
    {
        var resp = await Client.GetAsync("/api/v1/barcode/lookup/6901028075138");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

/// <summary>
/// 改密码**失败路径**的集成测试：只验证「该拒的都拒了」，不改成功。
/// 成功路径在 <see cref="ChangePasswordSuccessIntegrationTests"/>（单独一个类）。
/// </summary>
public class ChangePasswordIntegrationTests : IntegrationTestBase
{
    public ChangePasswordIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task 修改密码_原密码错或新密码太弱都被拦下且旧密码仍可用()
    {
        await LoginAsAdminAsync();

        var wrongOld = await ReadBody(await PostJsonAsync("/api/v1/auth/change-password",
            new { oldPassword = "not-my-password", newPassword = "newpass1" }));
        Assert.NotEqual(0, wrongOld.GetProperty("code").GetInt32());

        // 新密码必须含数字与字母、长度 ≥ 6
        var weak = await ReadBody(await PostJsonAsync("/api/v1/auth/change-password",
            new { oldPassword = "123456", newPassword = "123456" }));
        Assert.NotEqual(0, weak.GetProperty("code").GetInt32());

        // 两次都该是「被拦住」，而不是「悄悄改成功了」——用旧密码还能登录取证
        var stillWorks = await ReadBody(await PostJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "123456" }));
        Assert.Equal(0, stillWorks.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 修改密码_未登录返回401()
    {
        var resp = await PostJsonAsync("/api/v1/auth/change-password",
            new { oldPassword = "123456", newPassword = "newpass1" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

/// <summary>
/// 改密码**成功路径**的集成测试。
///
/// **必须单独一个类**：它会真的把 <c>admin</c> 的密码改掉，而同一测试类共享一个工厂
/// （也就是共享一个 InMemory 库），留在别的类里会把同类的用例一起带坏 ——
/// 具体症状是「谁先跑谁说话」，失败信息还完全指不到改密码上。
/// </summary>
public class ChangePasswordSuccessIntegrationTests : IntegrationTestBase
{
    public ChangePasswordSuccessIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task 修改密码_成功后新密码可登录且旧密码失效()
    {
        await LoginAsAdminAsync();

        var changed = await ReadBody(await PostJsonAsync("/api/v1/auth/change-password",
            new { oldPassword = "123456", newPassword = "newpass1" }));
        Assert.Equal(0, changed.GetProperty("code").GetInt32());

        var withNew = await ReadBody(await PostJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "newpass1" }));
        Assert.Equal(0, withNew.GetProperty("code").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(withNew.GetProperty("data").GetProperty("token").GetString()));

        var withOld = await ReadBody(await PostJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = "123456" }));
        Assert.NotEqual(0, withOld.GetProperty("code").GetInt32());
    }
}
