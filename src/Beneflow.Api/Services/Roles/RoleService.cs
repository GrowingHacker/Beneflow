using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>角色与权限分配（操作均记日志）</summary>
public class RoleService : IRoleService
{
    private readonly AppDbContext _db;
    private readonly ILogService _logs;
    public RoleService(AppDbContext db, ILogService logs) { _db = db; _logs = logs; }

    public async Task<List<object>> ListAsync()
    {
        var roles = await _db.Roles.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        var counts = await (
            from ur in _db.UserRoles.AsNoTracking()
            join u in _db.Users on ur.UserId equals u.Id
            where !u.IsDeleted
            group ur by ur.RoleId into g
            select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

        return roles.Select(r => (object)new
        {
            id = r.Id, name = r.Name, code = r.Code, desc = r.Description ?? "",
            status = r.Status ? "启用" : "禁用", userCount = counts.GetValueOrDefault(r.Id),
        }).ToList();
    }

    public async Task<ApiResult<object>> CreateAsync(string name, string code, string? desc)
    {
        code = code?.Trim() ?? "";
        name = name?.Trim() ?? "";
        if (name.Length == 0 || code.Length == 0) return ApiResult<object>.Fail("请填写角色名称和编码");
        if (await _db.Roles.AnyAsync(r => r.Code == code)) return ApiResult<object>.Fail("角色编码已存在");
        var role = new Role { Name = name, Code = code, Description = desc };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();
        await _logs.WriteAsync("角色管理", "新增角色", $"{code}/{name}");
        await _db.SaveChangesAsync();
        return ApiResult<object>.Ok(new { id = role.Id });
    }

    public async Task<ApiResult> UpdateAsync(int id, string name, string? desc)
    {
        var role = await _db.Roles.FindAsync(id);
        if (role == null) return ApiResult.Fail("角色不存在");
        role.Name = name?.Trim() ?? role.Name;
        role.Description = desc;
        await _logs.WriteAsync("角色管理", "编辑角色", role.Code);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    /// <summary>删除角色：已有用户关联时禁止</summary>
    public async Task<ApiResult> DeleteAsync(int id)
    {
        if (await _db.UserRoles.AnyAsync(ur => ur.RoleId == id)) return ApiResult.Fail("该角色已关联用户，禁止删除，只能禁用");
        var role = await _db.Roles.FindAsync(id);
        if (role == null) return ApiResult.Fail("角色不存在");
        var rms = await _db.RoleMenus.Where(rm => rm.RoleId == id).ToListAsync();
        _db.RoleMenus.RemoveRange(rms);
        _db.Roles.Remove(role);
        await _logs.WriteAsync("角色管理", "删除角色", role.Code);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    /// <summary>权限分配：整组重写该角色的 RoleMenu（含所属目录）</summary>
    public async Task<ApiResult> SetPermissionsAsync(int roleId, List<int> menuIds)
    {
        var role = await _db.Roles.FindAsync(roleId);
        if (role == null) return ApiResult.Fail("角色不存在");

        var old = await _db.RoleMenus.Where(rm => rm.RoleId == roleId).ToListAsync();
        _db.RoleMenus.RemoveRange(old);

        if (menuIds.Count > 0)
        {
            var leaves = await _db.Menus.Where(m => menuIds.Contains(m.Id)).ToListAsync();
            var dirIds = new HashSet<int>();
            foreach (var m in leaves)
            {
                _db.RoleMenus.Add(new RoleMenu { RoleId = roleId, MenuId = m.Id });
                // 前端按页码排序勾选叶子，同步挂载其目录便于树形展示
                if (m.Type == "菜单")
                {
                    var dirName = MenuService.GroupOf(m.Sort);
                    if (!string.IsNullOrEmpty(dirName))
                    {
                        var dir = await _db.Menus.FirstOrDefaultAsync(mm => mm.Name == dirName && mm.Type == "目录");
                        if (dir != null) dirIds.Add(dir.Id);
                    }
                }
            }
            foreach (var dirId in dirIds)
                _db.RoleMenus.Add(new RoleMenu { RoleId = roleId, MenuId = dirId });
        }
        await _logs.WriteAsync("角色管理", "分配权限", $"{role.Code} 共 {menuIds.Count} 项");
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    public async Task<List<string>> PermissionCodesAsync(int roleId) =>
        await (from rm in _db.RoleMenus.AsNoTracking()
               join m in _db.Menus on rm.MenuId equals m.Id
               where rm.RoleId == roleId && m.Type == "菜单"
               select m.PermCode).Distinct().ToListAsync();

    public async Task<List<int>> MenuIdsAsync(int roleId) =>
        await _db.RoleMenus.AsNoTracking().Where(rm => rm.RoleId == roleId).Select(rm => rm.MenuId).ToListAsync();
}
