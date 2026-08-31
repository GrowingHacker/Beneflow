using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>用户管理：列表（含角色关联）/ 增改删 / 启停（操作均记日志）</summary>
public class UserService : IUserService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly ILogService _logs;
    private readonly AccountStatusCache _status;
    public UserService(AppDbContext db, ICurrentUser me, ILogService logs, AccountStatusCache status)
    { _db = db; _me = me; _logs = logs; _status = status; }

    public async Task<PagedResult<object>> ListAsync(string? keyword, int page, int pageSize)
    {
        var q =
            from u in _db.Users.AsNoTracking()
            join ur in _db.UserRoles on u.Id equals ur.UserId into urs
            from ur in urs.DefaultIfEmpty()
            join r in _db.Roles on ur.RoleId equals r.Id into rs
            from r in rs.DefaultIfEmpty()
            where !u.IsDeleted &&
                  (string.IsNullOrEmpty(keyword) || u.Username.Contains(keyword) || u.Name.Contains(keyword))
            orderby u.Id
            select new { u, RoleName = r != null ? r.Name : null, r.Code };

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(x => (object)new
        {
            id = x.u.Id, username = x.u.Username, name = x.u.Name,
            phone = x.u.Phone, role = x.RoleName ?? "", roleCode = x.Code,
            status = x.u.Status ? "启用" : "禁用", remark = x.u.Remark,
            createdAt = x.u.CreatedAt.ToString("yyyy-MM-dd"),
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    public async Task<ApiResult<object>> CreateAsync(UserCreateDto dto)
    {
        dto.Username = dto.Username?.Trim() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(dto.Username, "^[A-Za-z0-9]{4,20}$"))
            return ApiResult<object>.Fail("用户名须为 4-20 位字母或数字");
        if (string.IsNullOrEmpty(dto.Password) || dto.Password.Length < 6
            || !dto.Password.Any(char.IsDigit) || !dto.Password.Any(char.IsLetter))
            return ApiResult<object>.Fail("密码必须包含数字和字母，长度不少于 6 位");
        if (!string.IsNullOrEmpty(dto.Phone) &&
            !System.Text.RegularExpressions.Regex.IsMatch(dto.Phone, "^1[3-9]\\d{9}$"))
            return ApiResult<object>.Fail("手机号格式不正确");
        if (await _db.Roles.FindAsync(dto.RoleId) == null)
            return ApiResult<object>.Fail("请选择角色");
        if (await _db.Users.AnyAsync(u => !u.IsDeleted && u.Username == dto.Username))
            return ApiResult<object>.Fail($"用户名 {dto.Username} 已存在");

        var salt = PasswordHasher.NewSalt();
        var user = new UserInfo
        {
            Username = dto.Username, Salt = salt, PasswordHash = PasswordHasher.Hash(dto.Password, salt),
            Name = dto.Name?.Trim() ?? "", Phone = dto.Phone, Remark = dto.Remark,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = dto.RoleId });
        await _logs.WriteAsync("用户管理", "新增用户", $"{user.Username} {user.Name}");
        await _db.SaveChangesAsync();
        return ApiResult<object>.Ok(new { id = user.Id });
    }

    public async Task<ApiResult> UpdateAsync(int id, UserUpdateDto dto, bool isAdmin)
    {
        if (_me.Id != id && !isAdmin) return ApiResult.Fail("无权限操作他人账号");
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);
        if (user == null) return ApiResult.Fail("用户不存在");

        if (isAdmin)
        {
            if (dto.RoleId > 0 && await _db.Roles.FindAsync(dto.RoleId) == null)
                return ApiResult.Fail("所选角色不存在");
            if (!string.IsNullOrEmpty(dto.Phone) &&
                !System.Text.RegularExpressions.Regex.IsMatch(dto.Phone, "^1[3-9]\\d{9}$"))
                return ApiResult.Fail("手机号格式不正确");
        }

        if (!string.IsNullOrWhiteSpace(dto.Name)) user.Name = dto.Name.Trim();
        if (dto.Phone != null) user.Phone = dto.Phone;
        if (dto.Remark != null) user.Remark = dto.Remark;

        if (isAdmin && dto.RoleId > 0)
        {
            var links = await _db.UserRoles.Where(ur => ur.UserId == id).ToListAsync();
            _db.UserRoles.RemoveRange(links);
            _db.UserRoles.Add(new UserRole { UserId = id, RoleId = dto.RoleId });
        }
        // 重置密码（管理员）
        if (isAdmin && !string.IsNullOrEmpty(dto.Password))
        {
            if (dto.Password.Length < 6 || !dto.Password.Any(char.IsDigit) || !dto.Password.Any(char.IsLetter))
                return ApiResult.Fail("密码必须包含数字和字母，长度不少于 6 位");
            user.PasswordHash = PasswordHasher.Hash(dto.Password, user.Salt);
        }
        await _logs.WriteAsync("用户管理", "编辑用户", $"{user.Username}");
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    public async Task<ApiResult> ToggleAsync(int id, string status)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);
        if (user == null) return ApiResult.Fail("用户不存在");
        var next = status == "启用" ? "启用" : "禁用";
        if (id == _me.Id && next == "禁用") return ApiResult.Fail("不能禁用当前登录的账号");
        user.Status = next == "启用";
        await _logs.WriteAsync("用户管理", "启用/禁用用户", $"{user.Username} → {next}");
        await _db.SaveChangesAsync();
        _status.Invalidate(id);   // 立即生效：被禁用账号的旧 token 在下一次请求即被拦截
        return ApiResult.Ok();
    }

    public async Task<ApiResult> DeleteAsync(int id)
    {
        if (id == 1) return ApiResult.Fail("内置店主账号禁止删除");
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted);
        if (user == null) return ApiResult.Fail("用户不存在");
        user.IsDeleted = true;
        user.Status = false;
        await _logs.WriteAsync("用户管理", "删除用户", $"{user.Username}（逻辑删除）");
        await _db.SaveChangesAsync();
        _status.Invalidate(id);   // 被删账号的旧 token 立即失效
        return ApiResult.Ok();
    }
}
