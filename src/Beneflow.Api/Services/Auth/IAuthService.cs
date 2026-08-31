using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>认证与账号安全：登录校验、权限码下发、改密</summary>
public interface IAuthService
{
    Task<List<string>> GetPermissionCodesAsync(int userId);
    Task<ApiResult<object>> LoginAsync(string username, string password, string? ip);
    Task<ApiResult> ChangePasswordAsync(int userId, string oldPwd, string newPwd);
}
