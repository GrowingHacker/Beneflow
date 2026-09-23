using System.Net;
using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 基础资料与系统管理端点的上线前集成测试：商品分类 / 菜单 / 角色 / 用户 / 供应商详情 / 系统设置 /
/// 操作日志 / 看板手机地址。
///
/// 这些接口本身都是薄 CRUD，业务复杂度低 —— 但它们**全都要登录才能用**，
/// 且其中几个端点在控制器里做了与 Service 不同的就地校验（<c>MenusController</c> 的
/// 「菜单/按钮必须填权限码」、<c>RolesController</c> 的「名称和编码不能为空」）。
/// 这层校验单测看不见（单测直接调 Service，绕过了控制器），
/// 一旦控制器里的分支被改掉，页面上就是「填错了也能存进去」。
/// 所以这里重点断言「该拦的拦住了」以及「改了之后回查得到」。
/// </summary>
public class MasterDataIntegrationTests : IntegrationSeedBase
{
    public MasterDataIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    // ================= 商品分类 =================

    [Fact]
    public async Task 分类_新增后出现在列表里再删除()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITCAT");

        var created = await ExpectOk(await PostJsonAsync("/api/v1/categories", new { name }));
        var id = created.GetProperty("id").GetInt32();
        Assert.True(id > 0);

        var list = (await ReadBody(await Client.GetAsync("/api/v1/categories"))).GetProperty("data")
            .EnumerateArray().ToList();
        Assert.Contains(list, x => x.GetProperty("name").GetString() == name);

        Assert.Equal(0, (await ReadBody(await Client.DeleteAsync($"/api/v1/categories/{id}")))
            .GetProperty("code").GetInt32());
        var after = (await ReadBody(await Client.GetAsync("/api/v1/categories"))).GetProperty("data")
            .EnumerateArray().ToList();
        Assert.DoesNotContain(after, x => x.GetProperty("name").GetString() == name);
    }

    [Fact]
    public async Task 分类_名称为空或重名都被拦下()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITCAT2");

        var blank = await ReadBody(await PostJsonAsync("/api/v1/categories", new { name = "   " }));
        Assert.NotEqual(0, blank.GetProperty("code").GetInt32());
        Assert.Contains("不能为空", blank.GetProperty("message").GetString());

        await ExpectOk(await PostJsonAsync("/api/v1/categories", new { name }));
        var dup = await ReadBody(await PostJsonAsync("/api/v1/categories", new { name }));
        Assert.NotEqual(0, dup.GetProperty("code").GetInt32());
        Assert.Contains("已存在", dup.GetProperty("message").GetString());
    }

    // ================= 菜单 =================

    [Fact]
    public async Task 菜单_新增修改删除整条链路()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITMENU");

        // 目录类型不需要权限码
        var created = await ExpectOk(await PostJsonAsync("/api/v1/menus",
            new { name, type = "目录", sort = 90 }));
        var id = created.GetProperty("id").GetInt32();

        var tree = (await ReadBody(await Client.GetAsync("/api/v1/menus"))).GetProperty("data")
            .EnumerateArray().ToList();
        Assert.NotEmpty(tree);
        Assert.True(tree[0].TryGetProperty("children", out _), "菜单接口返回的是树结构，节点上应有 children");

        var renamed = name + "_改";
        Assert.Equal(0, (await ReadBody(await PutJsonAsync($"/api/v1/menus/{id}",
            new { name = renamed, permCode = "it:menu", sort = 91 }))).GetProperty("code").GetInt32());

        Assert.Equal(0, (await ReadBody(await Client.DeleteAsync($"/api/v1/menus/{id}")))
            .GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 菜单_菜单类型不填权限码被控制器拦下()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await PostJsonAsync("/api/v1/menus",
            new { name = NewTag("ITMENU2"), type = "菜单", sort = 92 }));

        // 控制器就地校验：单测直接调 Service 走不到这里
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("权限码", body.GetProperty("message").GetString());
    }

    // ================= 角色 =================

    [Fact]
    public async Task 角色_新增改权限删除且已关联用户时禁止删除()
    {
        await LoginAsAdminAsync();
        var code = NewTag("itrole");
        var name = NewTag("ITROLE");

        var created = await ExpectOk(await PostJsonAsync("/api/v1/roles", new { name, code, desc = "集成测试" }));
        var roleId = created.GetProperty("id").GetInt32();

        var roles = (await ReadBody(await Client.GetAsync("/api/v1/roles"))).GetProperty("data")
            .EnumerateArray().ToList();
        Assert.Contains(roles, x => x.GetProperty("id").GetInt32() == roleId);

        // 系统里必然有菜单。⚠️ 分配叶子菜单时服务端会**自动把所属「目录」一起挂上**
        // （RoleService.SetPermissionsAsync 里那段，前端按页码勾叶子、树形展示需要目录），
        // 所以回读的集合是「分配项 ∪ 其目录」，不能断言与分配项严格相等。
        static IEnumerable<JsonElement> Children(JsonElement root) =>
            root.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array
                ? ch.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        var tree = (await ReadBody(await Client.GetAsync("/api/v1/menus"))).GetProperty("data")
            .EnumerateArray().ToList();
        var treeIds = tree
            .SelectMany(r => new[] { r.GetProperty("id").GetInt32() }
                .Concat(Children(r).Select(c => c.GetProperty("id").GetInt32())))
            .ToList();

        // 取前两个叶子（不是目录）：目录是服务端自己补的，不该由测试指定
        var assigned = tree.SelectMany(Children).Select(c => c.GetProperty("id").GetInt32()).Take(2).ToList();
        Assert.NotEmpty(assigned);

        Assert.Equal(0, (await ReadBody(await PutJsonAsync($"/api/v1/roles/{roleId}/permissions",
            new { menuIds = assigned }))).GetProperty("code").GetInt32());

        var granted = (await ReadBody(await Client.GetAsync($"/api/v1/roles/{roleId}/menus"))).GetProperty("data")
            .EnumerateArray().Select(x => x.GetInt32()).OrderBy(x => x).ToList();
        // ① 分配了什么就回读到什么；② 回读的每一项都必须是系统里真实存在的菜单（防串号/脏 id）
        Assert.All(assigned, id => Assert.Contains(id, granted));
        Assert.All(granted, id => Assert.Contains(id, treeIds));

        // 改名字
        Assert.Equal(0, (await ReadBody(await PutJsonAsync($"/api/v1/roles/{roleId}",
            new { name = name + "_改", desc = "改过" }))).GetProperty("code").GetInt32());

        // 挂一个用户上去，再删角色 → 必须被拦（避免把在用角色删掉把人架空）
        var roleIdForUser = roleId;
        var username = "it" + Guid.NewGuid().ToString("N")[..8];
        await ExpectOk(await PostJsonAsync("/api/v1/users", new
        {
            username,
            password = "abc123",
            name = "集成测试用户",
            roleId = roleIdForUser,
        }));

        var blocked = await ReadBody(await Client.DeleteAsync($"/api/v1/roles/{roleId}"));
        Assert.NotEqual(0, blocked.GetProperty("code").GetInt32());
        Assert.Contains("已关联用户", blocked.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 角色_编码重复被拦下()
    {
        await LoginAsAdminAsync();
        var code = NewTag("itdup");
        await ExpectOk(await PostJsonAsync("/api/v1/roles", new { name = NewTag("ITROLE"), code }));

        var dup = await ReadBody(await PostJsonAsync("/api/v1/roles", new { name = NewTag("ITROLE"), code }));
        Assert.NotEqual(0, dup.GetProperty("code").GetInt32());
        Assert.Contains("已存在", dup.GetProperty("message").GetString());
    }

    // ================= 用户 =================

    [Fact]
    public async Task 用户_新增改启停删除整条链路()
    {
        await LoginAsAdminAsync();
        var roleId = (await ReadBody(await Client.GetAsync("/api/v1/roles"))).GetProperty("data")
            .EnumerateArray().First().GetProperty("id").GetInt32();
        var username = "it" + Guid.NewGuid().ToString("N")[..8];
        var name = NewTag("ITUSER");

        var created = await ExpectOk(await PostJsonAsync("/api/v1/users",
            new { username, password = "abc123", name, roleId }));
        var userId = created.GetProperty("id").GetInt32();

        JsonElement FindUser(JsonElement body) => body.GetProperty("data").GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("username").GetString() == username);

        var listed = FindUser(await ReadBody(await Client.GetAsync($"/api/v1/users?keyword={username}")));
        Assert.Equal(name, listed.GetProperty("name").GetString());
        Assert.Equal("启用", listed.GetProperty("status").GetString());

        // 改资料
        Assert.Equal(0, (await ReadBody(await PutJsonAsync($"/api/v1/users/{userId}",
            new { name = name + "_改", phone = "13800001234", roleId, remark = "改过" }))).GetProperty("code").GetInt32());
        listed = FindUser(await ReadBody(await Client.GetAsync($"/api/v1/users?keyword={username}")));
        Assert.Equal(name + "_改", listed.GetProperty("name").GetString());

        // 禁用
        Assert.Equal(0, (await ReadBody(await PutJsonAsync($"/api/v1/users/{userId}/status",
            new { status = "禁用" }))).GetProperty("code").GetInt32());
        listed = FindUser(await ReadBody(await Client.GetAsync($"/api/v1/users?keyword={username}")));
        Assert.Equal("禁用", listed.GetProperty("status").GetString());

        // 删除
        Assert.Equal(0, (await ReadBody(await Client.DeleteAsync($"/api/v1/users/{userId}")))
            .GetProperty("code").GetInt32());
        var remaining = (await ReadBody(await Client.GetAsync($"/api/v1/users?keyword={username}")))
            .GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.DoesNotContain(remaining, x => x.GetProperty("username").GetString() == username);
    }

    [Fact]
    public async Task 用户_用户名与密码不合规被拦下且给中文原因()
    {
        await LoginAsAdminAsync();
        var roleId = (await ReadBody(await Client.GetAsync("/api/v1/roles"))).GetProperty("data")
            .EnumerateArray().First().GetProperty("id").GetInt32();

        var badName = await ReadBody(await PostJsonAsync("/api/v1/users",
            new { username = "ab", password = "abc123", name = "x", roleId }));
        Assert.NotEqual(0, badName.GetProperty("code").GetInt32());
        Assert.Contains("4-20", badName.GetProperty("message").GetString());

        var badPwd = await ReadBody(await PostJsonAsync("/api/v1/users",
            new { username = "it" + Guid.NewGuid().ToString("N")[..8], password = "123456", name = "x", roleId }));
        Assert.NotEqual(0, badPwd.GetProperty("code").GetInt32());
        Assert.Contains("数字和字母", badPwd.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 用户_重置密码为默认值后默认密码可登录且原密码失效()
    {
        await LoginAsAdminAsync();
        var roleId = (await ReadBody(await Client.GetAsync("/api/v1/roles"))).GetProperty("data")
            .EnumerateArray().First().GetProperty("id").GetInt32();

        var username = "it" + Guid.NewGuid().ToString("N")[..8];
        var created = await ExpectOk(await PostJsonAsync("/api/v1/users",
            new { username, password = "origin123", name = "重置密码", roleId }));
        var userId = created.GetProperty("id").GetInt32();

        // 重置：管理员**不提交任何密码**，落库的值由后端写死成默认密码 ——
        // 所以它也绕开了上面那条「新密码必须含数字和字母」的规则（默认密码全数字）。
        Assert.Equal(0, (await ReadBody(await PostAsync($"/api/v1/users/{userId}/reset-password")))
            .GetProperty("code").GetInt32());

        await LoginAsAsync(username, "123456");     // 默认密码能登进去

        var stale = await ReadBody(await PostJsonAsync("/api/v1/auth/login",
            new { username, password = "origin123" }));
        Assert.NotEqual(0, stale.GetProperty("code").GetInt32());   // 原密码同时失效
    }

    // ================= 供应商：详情与编辑 =================

    [Fact]
    public async Task 供应商_详情与编辑后回查一致()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITSUPD");
        var supplierId = await SeedSupplierAsync(name);

        var detail = (await ReadBody(await Client.GetAsync($"/api/v1/suppliers/{supplierId}"))).GetProperty("data");
        Assert.Equal(name, detail.GetProperty("name").GetString());

        var newName = name + "_改";
        Assert.Equal(0, (await ReadBody(await PutJsonAsync($"/api/v1/suppliers/{supplierId}",
            new { name = newName, contact = "李经理", phone = "13500001111", address = "珠海市香洲区" })))
            .GetProperty("code").GetInt32());

        var after = (await ReadBody(await Client.GetAsync($"/api/v1/suppliers/{supplierId}"))).GetProperty("data");
        Assert.Equal(newName, after.GetProperty("name").GetString());
        Assert.Equal("李经理", after.GetProperty("contact").GetString());
        Assert.Equal("13500001111", after.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task 供应商_已有采购记录时禁止删除()
    {
        await LoginAsAdminAsync();
        var supplierId = await SeedSupplierAsync(NewTag("ITSUPDEL"));
        var productId = await SeedProductAsync(NewTag("ITSUPP"), salePrice: 5m, costPrice: 2m);
        await SeedPurchaseAsync(supplierId, (productId, 1m, 2m));

        var blocked = await ReadBody(await Client.DeleteAsync($"/api/v1/suppliers/{supplierId}"));
        Assert.NotEqual(0, blocked.GetProperty("code").GetInt32());
        Assert.Contains("采购记录", blocked.GetProperty("message").GetString());
    }

    // ================= 系统设置 =================

    [Fact]
    public async Task 系统设置_读取分组齐全且保存后能读回()
    {
        await LoginAsAdminAsync();

        var before = (await ReadBody(await Client.GetAsync("/api/v1/settings"))).GetProperty("data");
        foreach (var group in new[] { "shop", "sale", "receipt", "stock", "units", "payMethods" })
            Assert.True(before.TryGetProperty(group, out _), $"设置应包含 {group} 组");

        var newName = NewTag("ITSHOP");
        Assert.Equal(0, (await ReadBody(await PutJsonAsync("/api/v1/settings",
            new { shop = new { name = newName, phone = "020-12345678", address = "测试地址", logo = "" } })))
            .GetProperty("code").GetInt32());

        var after = (await ReadBody(await Client.GetAsync("/api/v1/settings"))).GetProperty("data");
        Assert.Equal(newName, after.GetProperty("shop").GetProperty("name").GetString());

        // 顺手确认「保存设置」真的落了操作日志（日志页要能看到谁改的）
        var logs = (await ReadBody(await Client.GetAsync("/api/v1/logs?keyword=保存系统设置"))).GetProperty("data");
        Assert.True(logs.GetProperty("total").GetInt32() >= 1);
    }

    // ================= 操作日志 =================

    [Fact]
    public async Task 操作日志_可查询且禁止写入()
    {
        await LoginAsAdminAsync();
        // 先制造一条日志（新增分类会写「新增分类」）
        var name = NewTag("ITLOGCAT");
        await ExpectOk(await PostJsonAsync("/api/v1/categories", new { name }));

        var body = await ReadBody(await Client.GetAsync("/api/v1/logs?page=1&pageSize=5"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.True(data.GetProperty("total").GetInt32() >= 1);

        var row = data.GetProperty("list").EnumerateArray().First();
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("module").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("action").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("createdAt").GetString()));

        // 日志只读：控制器上那个 POST 是显式的「防误用」桩，必须返回 404 而不是悄悄成功
        var post = await Client.PostAsync("/api/v1/logs", null);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    // ================= 看板手机地址 =================

    [Fact]
    public async Task 看板手机地址_返回局域网地址()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await Client.GetAsync("/api/v1/dashboard/mobile-url"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());

        var data = body.GetProperty("data");
        var url = data.GetProperty("url").GetString();
        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.Contains("/mobile/purchase.html", url);
        Assert.True(data.TryGetProperty("lanIp", out var ip));
        Assert.False(string.IsNullOrWhiteSpace(ip.GetString()));
        Assert.True(data.TryGetProperty("https", out _));
    }
}
