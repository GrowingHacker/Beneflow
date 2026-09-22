using System.Text.Json;
using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>供应商管理</summary>
public interface ISupplierService
{
    Task<PagedResult<SupplierListItemDto>> ListAsync(string? keyword, int page, int pageSize);
    Task<ApiResult<SupplierDetailDto>> GetAsync(int id);
    Task<ApiResult<IdResultDto>> CreateAsync(SupplierUpsertDto dto);
    Task<ApiResult> UpdateAsync(int id, JsonElement body);
    Task<ApiResult> DeleteAsync(int id);
}
