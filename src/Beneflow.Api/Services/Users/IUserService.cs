using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>用户管理</summary>
public interface IUserService
{
    Task<PagedResult<object>> ListAsync(string? keyword, int page, int pageSize);
    Task<ApiResult<object>> CreateAsync(UserCreateDto dto);
    Task<ApiResult> UpdateAsync(int id, UserUpdateDto dto, bool isAdmin);
    Task<ApiResult> ToggleAsync(int id, string status);
    Task<ApiResult> DeleteAsync(int id);
}
