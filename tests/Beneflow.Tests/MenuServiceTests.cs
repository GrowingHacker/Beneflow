using Beneflow.Api.Models.Entities;
using Beneflow.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 菜单维护单元测试：Sort→目录归属映射、树形组装、增删改与删除保护。
/// </summary>
public class MenuServiceTests : TestBase
{
    [Theory]
    [InlineData(0, "")]            // 首页看板：顶级
    [InlineData(1, "销售管理")]
    [InlineData(3, "销售管理")]
    [InlineData(4, "商品管理")]
    [InlineData(6, "采购管理")]
    [InlineData(10, "库存管理")]
    [InlineData(13, "")]           // 报表：顶级
    [InlineData(14, "系统管理")]
    [InlineData(99, "系统管理")]
    public void GroupOf_MapsSortToDirectory(int sort, string expected) =>
        Assert.Equal(expected, MenuService.GroupOf(sort));

    [Fact]
    public async Task TreeAsync_BuildsHierarchy()
    {
        var dir = new Menu { Name = "销售管理", Type = "目录", Sort = 0 };
        Db.Menus.Add(dir);
        await Db.SaveChangesAsync();
        var child = new Menu { Name = "收银台", Type = "菜单", PermCode = "sales", Sort = 1, ParentId = dir.Id };
        var top = new Menu { Name = "首页看板", Type = "菜单", PermCode = "dashboard", Sort = 0 };
        Db.Menus.AddRange(child, top);
        await Db.SaveChangesAsync();

        var tree = await MenuSvc.TreeAsync();
        var root = tree.First(x => Prop<string>(x, "name") == "销售管理");
        var children = Prop<IEnumerable<object>>(root, "children")!.ToList();

        Assert.Single(children);
        Assert.Equal("收银台", Prop<string>(children[0], "name"));
        Assert.Equal("sales", Prop<string>(children[0], "permCode"));
    }

    [Fact]
    public async Task TreeAsync_InfersParentBySort()
    {
        // 未设置 ParentId 的菜单，按 Sort 推断归属目录
        var dir = new Menu { Name = "库存管理", Type = "目录", Sort = 0 };
        var menu = new Menu { Name = "库存查询", Type = "菜单", PermCode = "stock", Sort = 8 };
        Db.Menus.AddRange(dir, menu);
        await Db.SaveChangesAsync();

        var tree = await MenuSvc.TreeAsync();
        var root = tree.First(x => Prop<string>(x, "name") == "库存管理");
        var children = Prop<IEnumerable<object>>(root, "children")!.ToList();

        Assert.Single(children);
        Assert.Equal("库存查询", Prop<string>(children[0], "name"));
    }

    [Fact]
    public async Task CreateAsync_Success()
    {
        var r = await MenuSvc.CreateAsync("会员管理", "菜单", "member", null, 20);
        Assert.Equal(0, r.Code);

        var id = GetResultDataProp<int>(r.Data!, "id");
        var m = await Db.Menus.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal("member", m.PermCode);
        Assert.True(m.Visible);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_ReturnsError()
    {
        var r = await MenuSvc.CreateAsync("  ", "菜单", "x", null, 1);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("请填写菜单名称", r.Message);
    }

    [Fact]
    public async Task CreateAsync_InvalidType_FallbackToMenu()
    {
        var r = await MenuSvc.CreateAsync("奇怪类型", "未知类型", "weird", null, 1);
        var id = GetResultDataProp<int>(r.Data!, "id");
        Assert.Equal("菜单", (await Db.Menus.AsNoTracking().FirstAsync(x => x.Id == id)).Type);
    }

    [Fact]
    public async Task UpdateAsync_ChangesFields()
    {
        var created = await MenuSvc.CreateAsync("待改菜单", "菜单", "old", null, 5);
        var id = GetResultDataProp<int>(created.Data!, "id");

        var r = await MenuSvc.UpdateAsync(id, "已改菜单", "new", 9);
        Assert.Equal(0, r.Code);

        var m = await Db.Menus.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.Equal("已改菜单", m.Name);
        Assert.Equal("new", m.PermCode);
        Assert.Equal(9, m.Sort);
    }

    [Fact]
    public async Task UpdateAsync_NotExist_ReturnsError()
    {
        var r = await MenuSvc.UpdateAsync(9999, "不存在", null, null);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("菜单不存在", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_WithChildren_ReturnsError()
    {
        var parent = await MenuSvc.CreateAsync("父目录", "目录", null, null, 0);
        var parentId = GetResultDataProp<int>(parent.Data!, "id");
        await MenuSvc.CreateAsync("子菜单", "菜单", "child", parentId, 1);

        var r = await MenuSvc.DeleteAsync(parentId);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("存在子菜单", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_ReferencedByRole_ReturnsError()
    {
        var created = await MenuSvc.CreateAsync("被引用的菜单", "菜单", "used", null, 5);
        var id = GetResultDataProp<int>(created.Data!, "id");
        Db.RoleMenus.Add(new RoleMenu { RoleId = CashierRoleId, MenuId = id });
        await Db.SaveChangesAsync();

        var r = await MenuSvc.DeleteAsync(id);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("已被角色引用", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_StandaloneMenu_Removed()
    {
        var created = await MenuSvc.CreateAsync("临时菜单", "菜单", "tmp", null, 5);
        var id = GetResultDataProp<int>(created.Data!, "id");

        var r = await MenuSvc.DeleteAsync(id);
        Assert.Equal(0, r.Code);
        Assert.False(await Db.Menus.AnyAsync(m => m.Id == id));
    }

    [Fact]
    public async Task DeleteAsync_NotExist_ReturnsError()
    {
        var r = await MenuSvc.DeleteAsync(9999);
        Assert.NotEqual(0, r.Code);
    }
}
