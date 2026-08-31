using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Controllers;

/// <summary>
/// 认证接口：登录（JWT）+ 修改密码。
/// </summary>
[Route("api/v1/auth")]
public class AuthController : BaseApiController
{
    private readonly IAuthService _auth;
    private readonly ICurrentUser _me;
    private readonly ILogService _logs;
    private readonly AppDbContext _db;

    public AuthController(IAuthService auth, ICurrentUser me, ILogService logs, AppDbContext db)
    {
        _auth = auth;
        _me = me;
        _logs = logs;
        _db = db;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ApiResult<object>> Login([FromBody] LoginDto dto)
    {
        var result = await _auth.LoginAsync(dto.Username ?? "", dto.Password ?? "", _me.ClientIp);
        return result;
    }

    [HttpPost("change-password")]
    public async Task<ApiResult> ChangePassword([FromBody] ChangePasswordDto dto)
        => await _auth.ChangePasswordAsync(UserId(), dto.OldPassword, dto.NewPassword);

    [HttpPost("logout")]
    public async Task<ApiResult> Logout()
    {
        if (_me.Id > 0)
        {
            await _logs.WriteAsync("认证管理", "退出登录", null);
            await _db.SaveChangesAsync();
        }
        return ApiResult.Ok();
    }

    private int UserId() =>
        int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;
}
