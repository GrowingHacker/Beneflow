using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>菜单维护</summary>
public interface IMenuService
{
    Task<List<object>> TreeAsync();
    Task<ApiResult<object>> CreateAsync(string name, string type, string? permCode, int? parentId, int sort);
    Task<ApiResult> UpdateAsync(int id, string? name, string? permCode, int? sort);
    Task<ApiResult> DeleteAsync(int id);
}
