using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>角色与权限分配</summary>
public interface IRoleService
{
    Task<List<object>> ListAsync();
    Task<ApiResult<object>> CreateAsync(string name, string code, string? desc);
    Task<ApiResult> UpdateAsync(int id, string name, string? desc);
    Task<ApiResult> DeleteAsync(int id);
    Task<ApiResult> SetPermissionsAsync(int roleId, List<int> menuIds);
    Task<List<string>> PermissionCodesAsync(int roleId);
    Task<List<int>> MenuIdsAsync(int roleId);
}
