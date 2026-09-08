using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 角色与权限分配单元测试：角色 CRUD、删除保护、权限整组重写、权限码读取。
/// </summary>
public class RoleServiceTests : TestBase
{
    /// <summary>建一套菜单：目录 + 两个叶子菜单</summary>
    private async Task<(int dirId, int salesId, int stockId)> SeedMenusAsync()
    {
        var dir = new Menu { Name = "销售管理", Type = "目录", Sort = 0 };
        var sales = new Menu { Name = "收银台", Type = "菜单", PermCode = "sales", Sort = 1, ParentId = null };
        var stock = new Menu { Name = "库存查询", Type = "菜单", PermCode = "stock", Sort = 8, ParentId = null };
        Db.Menus.AddRange(dir, sales, stock);
        await Db.SaveChangesAsync();
        return (dir.Id, sales.Id, stock.Id);
    }

    [Fact]
    public async Task ListAsync_IncludesUserCount()
    {
        var list = await RoleSvc.ListAsync();
        var owner = list.First(x => Prop<int>(x, "id") == OwnerRoleId);

        Assert.Equal("店主", Prop<string>(owner, "name"));
        Assert.Equal("owner", Prop<string>(owner, "code"));
        Assert.Equal(1, Prop<int>(owner, "userCount"));   // 种子里店主账号 1 人
        Assert.Equal("启用", Prop<string>(owner, "status"));
    }

    [Fact]
    public async Task CreateAsync_Success()
    {
        var r = await RoleSvc.CreateAsync("库管", "keeper", "管仓库");
        Assert.Equal(0, r.Code);

        var id = GetResultDataProp<int>(r.Data!, "id");
        var role = await Db.Roles.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal("keeper", role.Code);
        Assert.True(role.Status);
    }

    [Fact]
    public async Task CreateAsync_DuplicateCode_ReturnsError()
    {
        var r = await RoleSvc.CreateAsync("另一个店主", "owner", null);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("角色编码已存在", r.Message);
    }

    [Fact]
    public async Task CreateAsync_EmptyNameOrCode_ReturnsError()
    {
        var noName = await RoleSvc.CreateAsync("  ", "code1", null);
        Assert.Contains("请填写角色名称和编码", noName.Message);

        var noCode = await RoleSvc.CreateAsync("有名字", "  ", null);
        Assert.Contains("请填写角色名称和编码", noCode.Message);
    }

    [Fact]
    public async Task UpdateAsync_ChangesName()
    {
        var r = await RoleSvc.UpdateAsync(CashierRoleId, "前台收银", "更新描述");
        Assert.Equal(0, r.Code);

        var role = await Db.Roles.AsNoTracking().FirstAsync(x => x.Id == CashierRoleId);
        Assert.Equal("前台收银", role.Name);
        Assert.Equal("更新描述", role.Description);
    }

    [Fact]
    public async Task UpdateAsync_NotExist_ReturnsError()
    {
        var r = await RoleSvc.UpdateAsync(9999, "不存在", null);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("角色不存在", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_WithUsers_ReturnsError()
    {
        var r = await RoleSvc.DeleteAsync(OwnerRoleId);   // 种子用户已关联店主角色
        Assert.NotEqual(0, r.Code);
        Assert.Contains("已关联用户", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_NoUsers_RemovesRoleAndRoleMenus()
    {
        var (_, salesId, _) = await SeedMenusAsync();
        await RoleSvc.SetPermissionsAsync(CashierRoleId, new List<int> { salesId });
        Assert.NotEmpty(await Db.RoleMenus.Where(rm => rm.RoleId == CashierRoleId).ToListAsync());

        var r = await RoleSvc.DeleteAsync(CashierRoleId);
        Assert.Equal(0, r.Code);

        Assert.False(await Db.Roles.AnyAsync(x => x.Id == CashierRoleId));
        Assert.Empty(await Db.RoleMenus.Where(rm => rm.RoleId == CashierRoleId).ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_NotExist_ReturnsError()
    {
        var r = await RoleSvc.DeleteAsync(9999);
        Assert.NotEqual(0, r.Code);
    }

    [Fact]
    public async Task SetPermissionsAsync_RewriteAndMountDirectory()
    {
        var (dirId, salesId, stockId) = await SeedMenusAsync();

        var r = await RoleSvc.SetPermissionsAsync(CashierRoleId, new List<int> { salesId });
        Assert.Equal(0, r.Code);

        // 授权叶子菜单的同时会挂载所属目录，便于前端树形回显
        var ids = await RoleSvc.MenuIdsAsync(CashierRoleId);
        Assert.Contains(salesId, ids);
        Assert.Contains(dirId, ids);

        // 整组重写：换成库存查询后，收银台不再保留
        await RoleSvc.SetPermissionsAsync(CashierRoleId, new List<int> { stockId });
        ids = await RoleSvc.MenuIdsAsync(CashierRoleId);
        Assert.DoesNotContain(salesId, ids);
        Assert.Contains(stockId, ids);

        var codes = await RoleSvc.PermissionCodesAsync(CashierRoleId);
        Assert.Equal(new[] { "stock" }, codes);
    }

    [Fact]
    public async Task SetPermissionsAsync_EmptyList_ClearsAll()
    {
        var (_, salesId, _) = await SeedMenusAsync();
        await RoleSvc.SetPermissionsAsync(CashierRoleId, new List<int> { salesId });

        var r = await RoleSvc.SetPermissionsAsync(CashierRoleId, new List<int>());
        Assert.Equal(0, r.Code);
        Assert.Empty(await RoleSvc.MenuIdsAsync(CashierRoleId));
    }

    [Fact]
    public async Task SetPermissionsAsync_RoleNotExist_ReturnsError()
    {
        var r = await RoleSvc.SetPermissionsAsync(9999, new List<int> { 1 });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("角色不存在", r.Message);
    }

    [Fact]
    public async Task PermissionCodesAsync_ExcludesDirectory()
    {
        var (dirId, salesId, _) = await SeedMenusAsync();
        Db.RoleMenus.AddRange(
            new RoleMenu { RoleId = CashierRoleId, MenuId = dirId },
            new RoleMenu { RoleId = CashierRoleId, MenuId = salesId });
        await Db.SaveChangesAsync();

        var codes = await RoleSvc.PermissionCodesAsync(CashierRoleId);
        Assert.Single(codes);
        Assert.Equal("sales", codes[0]);
    }
}
