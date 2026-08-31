using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>菜单维护：树形结构 + 增改删（操作记日志）</summary>
public class MenuService : IMenuService
{
    private readonly AppDbContext _db;
    private readonly ILogService _logs;
    public MenuService(AppDbContext db, ILogService logs) { _db = db; _logs = logs; }

    public async Task<List<object>> TreeAsync()
    {
        var menus = await _db.Menus.AsNoTracking().OrderBy(m => m.Sort).ThenBy(m => m.Id).ToListAsync();
        return BuildTree(menus, null);
    }

    /// <summary>有效父级：显式 ParentId 优先；未设置时按 Sort 推断所属目录（与角色授权 GroupOf 语义一致），目录与推断不到的菜单保持顶级</summary>
    private int? EffectiveParent(Menu m, Dictionary<string, Menu> dirByName)
    {
        if (m.ParentId.HasValue) return m.ParentId;
        if (m.Type == "目录") return null;
        var dirName = GroupOf(m.Sort);
        return dirName != "" && dirByName.TryGetValue(dirName, out var dir) ? dir.Id : null;
    }

    private List<object> BuildTree(List<Menu> all, int? parentId)
    {
        var dirByName = all.Where(m => m.Type == "目录").ToDictionary(m => m.Name);
        return all.Where(m => EffectiveParent(m, dirByName) == parentId).Select(m => (object)new
        {
            id = m.Id, name = m.Name, type = m.Type, permCode = m.PermCode,
            sort = m.Sort, visible = m.Visible,
            children = BuildTree(all, m.Id),
        }).ToList();
    }

    /// <summary>菜单 Sort → 所属目录名；返回空串表示顶级（dashboard=0、reports=13）</summary>
    public static string GroupOf(int sort) => sort switch
    {
        >= 1 and <= 3 => "销售管理",
        4 or 5 => "商品管理",
        6 or 7 => "采购管理",
        >= 8 and <= 12 => "库存管理",
        >= 14 => "系统管理",
        _ => "",
    };

    public async Task<ApiResult<object>> CreateAsync(string name, string type, string? permCode, int? parentId, int sort)
    {
        if (string.IsNullOrWhiteSpace(name)) return ApiResult<object>.Fail("请填写菜单名称");
        var m = new Menu { Name = name.Trim(), Type = type is "目录" or "菜单" or "按钮" ? type : "菜单", PermCode = permCode?.Trim() ?? "", ParentId = parentId, Sort = sort };
        _db.Menus.Add(m);
        await _db.SaveChangesAsync();
        await _logs.WriteAsync("菜单管理", "新增菜单", m.Name);
        await _db.SaveChangesAsync();
        return ApiResult<object>.Ok(new { id = m.Id });
    }

    public async Task<ApiResult> UpdateAsync(int id, string? name, string? permCode, int? sort)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m == null) return ApiResult.Fail("菜单不存在");
        if (!string.IsNullOrEmpty(name)) m.Name = name.Trim();
        if (permCode != null) m.PermCode = permCode.Trim();
        if (sort.HasValue) m.Sort = sort.Value;
        await _logs.WriteAsync("菜单管理", "编辑菜单", m.Name);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    public async Task<ApiResult> DeleteAsync(int id)
    {
        if (await _db.Menus.AnyAsync(m => m.ParentId == id)) return ApiResult.Fail("存在子菜单，禁止删除");
        if (await _db.RoleMenus.AnyAsync(rm => rm.MenuId == id)) return ApiResult.Fail("已被角色引用，禁止删除");
        var m = await _db.Menus.FindAsync(id);
        if (m == null) return ApiResult.Fail("菜单不存在");
        _db.Menus.Remove(m);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }
}
