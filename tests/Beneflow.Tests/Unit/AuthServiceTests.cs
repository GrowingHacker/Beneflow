using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 认证与账号安全单元测试：登录（含连续失败锁定）、权限码下发、修改密码。
/// 种子用户：admin / 123456（店主角色）。
/// </summary>
public class AuthServiceTests : TestBase
{
    private const string Pwd = "123456";

    [Fact]
    public async Task LoginAsync_Success_ReturnsTokenAndPermissions()
    {
        var r = await AuthSvc.LoginAsync("admin", Pwd, "127.0.0.1");
        Assert.Equal(0, r.Code);

        var data = r.Data!;
        Assert.False(string.IsNullOrWhiteSpace(Prop<string>(data, "token")));
        var user = Prop(data, "user")!;
        Assert.Equal("admin", Prop<string>(user, "username"));
        Assert.Equal("店主", Prop<string>(user, "role"));

        // 店主角色返回通配权限
        var perms = Prop<List<string>>(user, "permissions")!;
        Assert.Single(perms);
        Assert.Equal("*", perms[0]);
    }

    [Fact]
    public async Task LoginAsync_Success_ResetsFailedCount()
    {
        await AuthSvc.LoginAsync("admin", "wrong1", "127.0.0.1");
        await AuthSvc.LoginAsync("admin", "wrong2", "127.0.0.1");
        Assert.Equal(2, (await Db.Users.AsNoTracking().FirstAsync()).FailedCount);

        var r = await AuthSvc.LoginAsync("admin", Pwd, "127.0.0.1");
        Assert.Equal(0, r.Code);
        Assert.Equal(0, (await Db.Users.AsNoTracking().FirstAsync()).FailedCount);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_IncreasesFailedCount()
    {
        var r = await AuthSvc.LoginAsync("admin", "bad-pwd", "127.0.0.1");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("用户名或密码错误", r.Message);

        Assert.Equal(1, (await Db.Users.AsNoTracking().FirstAsync()).FailedCount);
        var loginLog = await Db.LoginLogs.AsNoTracking().OrderByDescending(l => l.Id).FirstAsync();
        Assert.False(loginLog.Success);
    }

    [Fact]
    public async Task LoginAsync_FiveFailures_LocksAccount()
    {
        for (var i = 0; i < 5; i++)
            await AuthSvc.LoginAsync("admin", $"bad-{i}", "127.0.0.1");

        var user = await Db.Users.AsNoTracking().FirstAsync();
        Assert.NotNull(user.LockedUntil);
        Assert.True(user.LockedUntil > DateTime.Now.AddMinutes(14));   // 锁定 15 分钟
        Assert.Equal(0, user.FailedCount);                             // 锁定后计数清零

        var locked = await AuthSvc.LoginAsync("admin", Pwd, "127.0.0.1");   // 正确密码也被拦
        Assert.NotEqual(0, locked.Code);
        Assert.Contains("已锁定", locked.Message);
    }

    [Fact]
    public async Task LoginAsync_DisabledAccount_ReturnsError()
    {
        var u = Db.Users.First();
        u.Status = false;
        await Db.SaveChangesAsync();

        var r = await AuthSvc.LoginAsync("admin", Pwd, "127.0.0.1");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("已被禁用", r.Message);
    }

    [Fact]
    public async Task LoginAsync_UserNotExist_ReturnsError()
    {
        var r = await AuthSvc.LoginAsync("nobody", Pwd, "127.0.0.1");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("用户名或密码错误", r.Message);

        var loginLog = await Db.LoginLogs.AsNoTracking().OrderByDescending(l => l.Id).FirstAsync();
        Assert.Null(loginLog.UserId);
        Assert.Equal("用户不存在", loginLog.Message);
    }

    [Fact]
    public async Task LoginAsync_DeletedUser_TreatedAsNotExist()
    {
        var u = Db.Users.First();
        u.IsDeleted = true;
        await Db.SaveChangesAsync();

        var r = await AuthSvc.LoginAsync("admin", Pwd, "127.0.0.1");
        Assert.NotEqual(0, r.Code);
    }

    // ================= 权限码 =================

    [Fact]
    public async Task GetPermissionCodesAsync_Owner_ReturnsWildcard()
    {
        var codes = await AuthSvc.GetPermissionCodesAsync(UserId);
        Assert.Equal(new[] { "*" }, codes);
    }

    [Fact]
    public async Task GetPermissionCodesAsync_NormalRole_ReturnsMenuCodes()
    {
        // 收银员角色授权：目录「销售管理」+ 菜单「收银台」(sales) + 菜单「库存查询」(stock)
        var dir = new Menu { Name = "销售管理", Type = "目录", Sort = 0 };
        var m1 = new Menu { Name = "收银台", Type = "菜单", PermCode = "sales", Sort = 1 };
        var m2 = new Menu { Name = "库存查询", Type = "菜单", PermCode = "stock", Sort = 8 };
        Db.Menus.AddRange(dir, m1, m2);
        await Db.SaveChangesAsync();

        var user = new UserInfo { Username = "cashier1", Name = "收银小李", Salt = "", PasswordHash = "" };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = CashierRoleId });
        Db.RoleMenus.AddRange(
            new RoleMenu { RoleId = CashierRoleId, MenuId = dir.Id },
            new RoleMenu { RoleId = CashierRoleId, MenuId = m1.Id },
            new RoleMenu { RoleId = CashierRoleId, MenuId = m2.Id });
        await Db.SaveChangesAsync();

        var codes = await AuthSvc.GetPermissionCodesAsync(user.Id);
        Assert.Equal(2, codes.Count);
        Assert.Contains("sales", codes);
        Assert.Contains("stock", codes);
        Assert.DoesNotContain("", codes);      // 目录没有权限码，不应出现在结果里
    }

    // ================= 修改密码 =================

    [Fact]
    public async Task ChangePasswordAsync_TooShort_ReturnsError()
    {
        var r = await AuthSvc.ChangePasswordAsync(UserId, Pwd, "ab1");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("不少于 6 位", r.Message);
    }

    [Fact]
    public async Task ChangePasswordAsync_NoDigit_ReturnsError()
    {
        var r = await AuthSvc.ChangePasswordAsync(UserId, Pwd, "abcdefg");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("数字和字母", r.Message);
    }

    [Fact]
    public async Task ChangePasswordAsync_WrongOldPassword_ReturnsError()
    {
        var r = await AuthSvc.ChangePasswordAsync(UserId, "000000", "abc123");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("原密码不正确", r.Message);
    }

    [Fact]
    public async Task ChangePasswordAsync_Success_NewPasswordWorks()
    {
        var r = await AuthSvc.ChangePasswordAsync(UserId, Pwd, "abc123");
        Assert.Equal(0, r.Code);

        var old = await AuthSvc.LoginAsync("admin", Pwd, "127.0.0.1");
        Assert.NotEqual(0, old.Code);

        var fresh = await AuthSvc.LoginAsync("admin", "abc123", "127.0.0.1");
        Assert.Equal(0, fresh.Code);
    }

    [Fact]
    public async Task ChangePasswordAsync_UserNotExist_ReturnsError()
    {
        var r = await AuthSvc.ChangePasswordAsync(9999, Pwd, "abc123");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("用户不存在", r.Message);
    }
}
