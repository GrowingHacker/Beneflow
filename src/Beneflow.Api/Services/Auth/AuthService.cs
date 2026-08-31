using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>
/// 认证与账号安全：登录校验（连续失败 5 次锁定 15 分钟）、权限码下发、改密。
/// </summary>
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AuthService> _log;

    public AuthService(AppDbContext db, IConfiguration cfg, ILogger<AuthService> log)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>按角色汇总权限码；店主返回 ["*"]</summary>
    public async Task<List<string>> GetPermissionCodesAsync(int userId)
    {
        // 店主角色直接给全量通配，保持前端 "*" 判断逻辑
        var isOwner = await (
            from ur in _db.UserRoles
            join r in _db.Roles on ur.RoleId equals r.Id
            where ur.UserId == userId && r.Code == "owner"
            select 1).AnyAsync();
        if (isOwner) return new List<string> { "*" };

        var codes = await (
            from ur in _db.UserRoles
            join rm in _db.RoleMenus on ur.RoleId equals rm.RoleId
            join m in _db.Menus on rm.MenuId equals m.Id
            where ur.UserId == userId && m.Type == "菜单"
            select m.PermCode).Distinct().ToListAsync();
        return codes.Where(c => !string.IsNullOrEmpty(c)).ToList();
    }

    public async Task<ApiResult<object>> LoginAsync(string username, string password, string? ip)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && !u.IsDeleted);
        if (user == null)
        {
            await WriteLoginLog(null, username, false, "用户不存在", ip);
            return ApiResult<object>.Fail("用户名或密码错误");
        }

        if (!user.Status)
        {
            await WriteLoginLog(user.Id, username, false, "账号已禁用", ip);
            WriteOpLog(user.Id, user.Username, ip, "认证管理", "登录失败", "账号已禁用");
            await _db.SaveChangesAsync();
            return ApiResult<object>.Fail("账号已被禁用，请联系店主");
        }

        if (user.LockedUntil.HasValue && user.LockedUntil.Value > DateTime.Now)
        {
            return ApiResult<object>.Fail($"密码错误次数过多，账号已锁定至 {user.LockedUntil:HH:mm}");
        }

        if (!PasswordHasher.Verify(password, user.Salt, user.PasswordHash))
        {
            user.FailedCount++;
            string msg = "用户名或密码错误";
            if (user.FailedCount >= 5)
            {
                user.LockedUntil = DateTime.Now.AddMinutes(15);
                user.FailedCount = 0;
                msg += $"，连续 5 次错误已锁定 15 分钟";
                _log.LogWarning("账号 {Username} 连续密码错误被锁定", username);
            }
            await WriteLoginLog(user.Id, username, false, msg, ip);
            WriteOpLog(user.Id, user.Username, ip, "认证管理", "登录失败", msg);
            await _db.SaveChangesAsync();
            return ApiResult<object>.Fail(msg);
        }

        user.FailedCount = 0;
        user.LockedUntil = null;
        await WriteLoginLog(user.Id, username, true, "登录成功", ip);
        WriteOpLog(user.Id, user.Username, ip, "认证管理", "登录成功", null);

        var role = await (
            from ur in _db.UserRoles
            join r in _db.Roles on ur.RoleId equals r.Id
            where ur.UserId == user.Id
            orderby ur.RoleId
            select r).FirstOrDefaultAsync();

        await _db.SaveChangesAsync(); // 清零计数

        var jwt = _cfg.GetSection("Jwt");
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(jwt["Secret"]!));
        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user.Username),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role?.Name ?? ""),
        };
        var expire = int.Parse(jwt["ExpireMinutes"] ?? "720");
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expire),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));

        var permissions = await GetPermissionCodesAsync(user.Id);
        var result = ApiResult<object>.Ok(new
        {
            token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token),
            user = new
            {
                id = user.Id,
                name = user.Name,
                username = user.Username,
                role = role?.Name ?? "",
                permissions,
            },
        });
        return result;
    }

    public async Task<ApiResult> ChangePasswordAsync(int userId, string oldPwd, string newPwd)
    {
        if (string.IsNullOrWhiteSpace(newPwd) || newPwd.Length < 6)
            return ApiResult.Fail("新密码长度不少于 6 位");
        if (!newPwd.Any(char.IsDigit) || !newPwd.Any(char.IsLetter))
            return ApiResult.Fail("新密码必须包含数字和字母");

        var user = await _db.Users.FindAsync(userId);
        if (user == null) return ApiResult.Fail("用户不存在");
        if (!PasswordHasher.Verify(oldPwd, user.Salt, user.PasswordHash))
            return ApiResult.Fail("原密码不正确");

        user.PasswordHash = PasswordHasher.Hash(newPwd, user.Salt);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    private async Task WriteLoginLog(int? userId, string username, bool success, string msg, string? ip)
    {
        _db.LoginLogs.Add(new LoginLog
        {
            UserId = userId, UserName = username, Success = success, Message = msg, IpAddress = ip,
        });
        if (!success) await _db.SaveChangesAsync(); // 失败也落库（成功时统一随登录流程保存）
    }

    /// <summary>写入操作日志（登录场景：认证前 CurrentUser 未填充，显式传用户/IP）</summary>
    private void WriteOpLog(int userId, string username, string? ip, string module, string action, string? target)
    {
        _db.OperationLogs.Add(new OperationLog
        {
            UserId = userId,
            UserName = username,
            IpAddress = ip,
            Module = module,
            Action = action,
            Target = target,
        });
    }
}
