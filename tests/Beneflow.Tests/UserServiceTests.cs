using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 用户管理单元测试：创建校验（用户名/密码/手机号/角色/重名）、编辑权限、启停、删除保护。
/// </summary>
public class UserServiceTests : TestBase
{
    private static UserCreateDto NewUser(string username = "cashier1", string pwd = "abc123",
        int roleId = 0, string? phone = null) =>
        new()
        {
            Username = username, Password = pwd, Name = "小张",
            Phone = phone, RoleId = roleId, Remark = "备注",
        };

    [Fact]
    public async Task ListAsync_ReturnsUserWithRole()
    {
        var page = await UserSvc.ListAsync(null, 1, 20);
        Assert.Equal(1, page.Total);
        Assert.Equal("admin", Prop<string>(page.List[0], "username"));
        Assert.Equal("店主", Prop<string>(page.List[0], "role"));
        Assert.Equal("owner", Prop<string>(page.List[0], "roleCode"));
        Assert.Equal("启用", Prop<string>(page.List[0], "status"));
    }

    [Fact]
    public async Task ListAsync_KeywordFilter()
    {
        var dto = NewUser("zhaoliu");
        dto.RoleId = CashierRoleId;
        await UserSvc.CreateAsync(dto);

        var page = await UserSvc.ListAsync("zhaoliu", 1, 20);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task CreateAsync_Success_CreatesUserRole()
    {
        var r = await UserSvc.CreateAsync(NewUser("cashier1", "abc123", CashierRoleId));
        Assert.Equal(0, r.Code);

        var id = GetResultDataProp<int>(r.Data!, "id");
        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == id);
        Assert.Equal("cashier1", user.Username);
        Assert.NotEqual("", user.Salt);
        Assert.NotEqual("", user.PasswordHash);

        Assert.True(await Db.UserRoles.AnyAsync(ur => ur.UserId == id && ur.RoleId == CashierRoleId));
    }

    [Fact]
    public async Task CreateAsync_InvalidUsername_ReturnsError()
    {
        var short1 = await UserSvc.CreateAsync(NewUser("ab", "abc123", CashierRoleId));
        Assert.Contains("4-20 位", short1.Message);

        var cn = await UserSvc.CreateAsync(NewUser("张三李四", "abc123", CashierRoleId));
        Assert.Contains("4-20 位", cn.Message);
    }

    [Fact]
    public async Task CreateAsync_WeakPassword_ReturnsError()
    {
        var noLetter = await UserSvc.CreateAsync(NewUser("cashier2", "123456", CashierRoleId));
        Assert.Contains("数字和字母", noLetter.Message);

        var noDigit = await UserSvc.CreateAsync(NewUser("cashier3", "abcdef", CashierRoleId));
        Assert.Contains("数字和字母", noDigit.Message);

        var tooShort = await UserSvc.CreateAsync(NewUser("cashier4", "ab1", CashierRoleId));
        Assert.Contains("数字和字母", tooShort.Message);
    }

    [Fact]
    public async Task CreateAsync_InvalidPhone_ReturnsError()
    {
        var r = await UserSvc.CreateAsync(NewUser("cashier5", "abc123", CashierRoleId, "12345"));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("手机号格式", r.Message);
    }

    [Fact]
    public async Task CreateAsync_RoleNotExist_ReturnsError()
    {
        var r = await UserSvc.CreateAsync(NewUser("cashier6", "abc123", 9999));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("请选择角色", r.Message);
    }

    [Fact]
    public async Task CreateAsync_DuplicateUsername_ReturnsError()
    {
        await UserSvc.CreateAsync(NewUser("dupuser", "abc123", CashierRoleId));
        var dup = await UserSvc.CreateAsync(NewUser("dupuser", "abc123", CashierRoleId));

        Assert.NotEqual(0, dup.Code);
        Assert.Contains("已存在", dup.Message);
    }

    [Fact]
    public async Task CreateAsync_TrimsUsername()
    {
        var r = await UserSvc.CreateAsync(NewUser("  spaced  ", "abc123", CashierRoleId));
        Assert.Equal(0, r.Code);
        Assert.True(await Db.Users.AnyAsync(u => u.Username == "spaced"));
    }

    // ================= 编辑 =================

    [Fact]
    public async Task UpdateAsync_NotAdmin_CannotEditOthers()
    {
        var created = await UserSvc.CreateAsync(NewUser("other1", "abc123", CashierRoleId));
        var otherId = GetResultDataProp<int>(created.Data!, "id");

        // 当前用户（UserId）以非管理员身份修改他人
        var r = await UserSvc.UpdateAsync(otherId, new UserUpdateDto { Name = "改名" }, isAdmin: false);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("无权限", r.Message);
    }

    [Fact]
    public async Task UpdateAsync_Self_Allowed()
    {
        var r = await UserSvc.UpdateAsync(UserId, new UserUpdateDto { Name = "我自己", Phone = "13800001111" }, isAdmin: false);
        Assert.Equal(0, r.Code);

        var u = await Db.Users.AsNoTracking().FirstAsync(x => x.Id == UserId);
        Assert.Equal("我自己", u.Name);
    }

    [Fact]
    public async Task UpdateAsync_Admin_ChangesRole()
    {
        var created = await UserSvc.CreateAsync(NewUser("rolechange", "abc123", CashierRoleId));
        var id = GetResultDataProp<int>(created.Data!, "id");

        var r = await UserSvc.UpdateAsync(id, new UserUpdateDto { RoleId = OwnerRoleId }, isAdmin: true);
        Assert.Equal(0, r.Code);

        var links = await Db.UserRoles.AsNoTracking().Where(ur => ur.UserId == id).ToListAsync();
        Assert.Single(links);                       // 旧关联被移除，只保留新的
        Assert.Equal(OwnerRoleId, links[0].RoleId);
    }

    [Fact]
    public async Task UpdateAsync_Admin_ResetPassword()
    {
        var created = await UserSvc.CreateAsync(NewUser("resetpwd", "abc123", CashierRoleId));
        var id = GetResultDataProp<int>(created.Data!, "id");

        var weak = await UserSvc.UpdateAsync(id, new UserUpdateDto { Password = "111" }, isAdmin: true);
        Assert.Contains("数字和字母", weak.Message);

        var ok = await UserSvc.UpdateAsync(id, new UserUpdateDto { Password = "xyz789" }, isAdmin: true);
        Assert.Equal(0, ok.Code);

        var login = await AuthSvc.LoginAsync("resetpwd", "xyz789", "127.0.0.1");
        Assert.Equal(0, login.Code);
    }

    [Fact]
    public async Task UpdateAsync_Admin_InvalidPhone_ReturnsError()
    {
        var created = await UserSvc.CreateAsync(NewUser("badphone", "abc123", CashierRoleId));
        var id = GetResultDataProp<int>(created.Data!, "id");

        var r = await UserSvc.UpdateAsync(id, new UserUpdateDto { Phone = "010-1234" }, isAdmin: true);
        Assert.NotEqual(0, r.Code);
    }

    [Fact]
    public async Task UpdateAsync_NotExist_ReturnsError()
    {
        var r = await UserSvc.UpdateAsync(9999, new UserUpdateDto { Name = "x" }, isAdmin: true);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("用户不存在", r.Message);
    }

    // ================= 启停与删除 =================

    [Fact]
    public async Task ToggleAsync_DisableThenEnable()
    {
        var created = await UserSvc.CreateAsync(NewUser("toggle1", "abc123", CashierRoleId));
        var id = GetResultDataProp<int>(created.Data!, "id");

        Assert.Equal(0, (await UserSvc.ToggleAsync(id, "禁用")).Code);
        Assert.False((await Db.Users.AsNoTracking().FirstAsync(u => u.Id == id)).Status);

        Assert.Equal(0, (await UserSvc.ToggleAsync(id, "启用")).Code);
        Assert.True((await Db.Users.AsNoTracking().FirstAsync(u => u.Id == id)).Status);
    }

    [Fact]
    public async Task ToggleAsync_CannotDisableSelf()
    {
        var r = await UserSvc.ToggleAsync(UserId, "禁用");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("不能禁用当前登录的账号", r.Message);
    }

    [Fact]
    public async Task ToggleAsync_NotExist_ReturnsError()
    {
        var r = await UserSvc.ToggleAsync(9999, "禁用");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("用户不存在", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_BuiltinOwner_Forbidden()
    {
        var r = await UserSvc.DeleteAsync(1);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("禁止删除", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_SoftDelete()
    {
        var created = await UserSvc.CreateAsync(NewUser("todelete", "abc123", CashierRoleId));
        var id = GetResultDataProp<int>(created.Data!, "id");

        var r = await UserSvc.DeleteAsync(id);
        Assert.Equal(0, r.Code);

        var u = await Db.Users.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.True(u.IsDeleted);
        Assert.False(u.Status);
        Assert.False(await Db.Users.AsNoTracking().AnyAsync(x => x.Id == id && !x.IsDeleted));
    }

    [Fact]
    public async Task DeleteAsync_NotExist_ReturnsError()
    {
        var r = await UserSvc.DeleteAsync(9999);
        Assert.NotEqual(0, r.Code);
    }
}
