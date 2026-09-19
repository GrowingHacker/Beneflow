using System.Net;
using System.Text.Json;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 「演示数据提示 + 初始化数据」的集成测试（**只读的守卫类**）：路由与鉴权、响应形状。
///
/// ⚠️ 破坏性用例（真正执行初始化）必须放到另一个测试类：
/// 同一个测试类共享一个工厂、也就是共享一个 InMemory 库，破坏性用例会把同类里其他用例的数据清掉
/// —— 本类第一版就是这么挂的（状态断言读到 false、商品断言读到 0）。
/// 需要执行清空的用例见 <see cref="DemoDataClearExecuteIntegrationTests"/>。
/// </summary>
public class DemoDataClearIntegrationTests : IntegrationTestBase
{
    public DemoDataClearIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    /// <summary>
    /// 读一个列表接口的元素个数：列表接口形状不统一 —— 多数是分页外壳（取 total），
    /// 角色/菜单是裸数组（取长度），这里一并兜住，免得断言写成两套。
    /// </summary>
    private async Task<int> CountOfAsync(string path)
    {
        var data = await ExpectOk(await Client.GetAsync(path));
        return data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : data.GetProperty("total").GetInt32();
    }

    [Fact]
    public async Task 未登录读演示数据状态返回401()
    {
        var resp = await Client.GetAsync("/api/v1/settings/demo-data");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 种子库读到的状态是仍是演示数据()
    {
        await LoginAsAdminAsync();

        // 种子库没有 demo 标记行，也必须报「是演示数据」，否则用户看不到初始化入口
        var data = await ExpectOk(await Client.GetAsync("/api/v1/settings/demo-data"));
        Assert.True(data.GetProperty("isDemoData").GetBoolean());
    }

    [Fact]
    public async Task 非店主不能初始化数据()
    {
        await LoginAsAsync("cashier", "123456");
        Assert.True(await CountOfAsync("/api/v1/products") > 0, "种子库应有演示商品");

        var body = await ReadBody(await PostJsonAsync("/api/v1/settings/demo-data/clear", new { confirm = "清空" }));

        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("店主", body.GetProperty("message").GetString()!);
        Assert.True(await CountOfAsync("/api/v1/products") > 0, "被拦下就不该清任何数据");
    }

    [Fact]
    public async Task 确认文字不对时店主也被拦下()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await PostJsonAsync("/api/v1/settings/demo-data/clear", new { confirm = "初始化" }));

        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.True(await CountOfAsync("/api/v1/products") > 0);
    }
}

/// <summary>
/// 真正执行初始化（清空）的集成测试。**单独一个类** ⇒ 独立的工厂与 InMemory 库，
/// 不会把同类只读用例的数据清掉。
/// </summary>
public class DemoDataClearExecuteIntegrationTests : IntegrationTestBase
{
    public DemoDataClearExecuteIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private async Task<int> CountOfAsync(string path)
    {
        var data = await ExpectOk(await Client.GetAsync(path));
        return data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : data.GetProperty("total").GetInt32();
    }

    [Fact]
    public async Task 店主确认后业务数据清空且账号保留提示消失()
    {
        await LoginAsAdminAsync();
        Assert.True(await CountOfAsync("/api/v1/products") > 0, "种子库应有演示商品");

        var data = await ExpectOk(await PostJsonAsync("/api/v1/settings/demo-data/clear", new { confirm = "清空" }));
        // InMemory（非关系库）没有物理备份能力：两个字段都应为 null，而不是假报一个路径
        Assert.Equal(JsonValueKind.Null, data.GetProperty("backupPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("backupError").ValueKind);

        // 换端点回查：业务数据都空了
        foreach (var path in new[] { "/api/v1/products", "/api/v1/suppliers", "/api/v1/sales", "/api/v1/purchases" })
            Assert.Equal(0, await CountOfAsync(path));

        // 账号 / 角色 / 菜单（系统侧）必须还在
        Assert.True(await CountOfAsync("/api/v1/users") > 0);
        Assert.True(await CountOfAsync("/api/v1/roles") > 0);
        Assert.True(await CountOfAsync("/api/v1/menus") > 0);

        // 状态翻转：前端常驻提示据此消失
        var status = await ExpectOk(await Client.GetAsync("/api/v1/settings/demo-data"));
        Assert.False(status.GetProperty("isDemoData").GetBoolean());

        // 换账号仍能登录（清空没碰账号与角色关联，否则初始化完就把自己锁门外了）
        await LoginAsAsync("cashier", "123456");
    }
}
