using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>库存管理：实时库存 / 库存预警 / 临期商品 / 盘点单 / 库存流水</summary>
public interface IStockService
{
    Task<object> InventoryListAsync(string? keyword, string? status, int page, int pageSize);
    Task<PagedResult<object>> CheckListAsync(int page, int pageSize);
    Task<ApiResult<object>> CreateCheckAsync(CreateStockCheckDto dto);
    Task<ApiResult> ConfirmCheckAsync(int id);
    Task<object> ExpiryListAsync(string? tag, int page, int pageSize);
    Task<ApiResult> MarkProcessedAsync(long[] ids);
    Task<List<object>> WarningsAsync();
    Task<PagedResult<object>> LogListAsync(string? keyword, string? changeType, int page, int pageSize);
}
