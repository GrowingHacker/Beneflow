using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>库存管理：实时库存 / 库存预警 / 临期商品 / 盘点单 / 库存流水</summary>
public interface IStockService
{
    Task<object> InventoryListAsync(string? keyword, string? status, int page, int pageSize);
    /// <summary>导出实时库存全量（按 keyword/status 筛选，不分页）</summary>
    Task<List<Dictionary<string, object?>>> ExportInventoryAsync(string? keyword, string? status);
    Task<PagedResult<object>> CheckListAsync(int page, int pageSize);
    Task<ApiResult<object>> CreateCheckAsync(CreateStockCheckDto dto);
    Task<ApiResult> ConfirmCheckAsync(int id);
    Task<object> ExpiryListAsync(string? tag, int page, int pageSize);
    Task<ApiResult> MarkProcessedAsync(long[] ids);
    Task<List<object>> WarningsAsync();
    Task<PagedResult<object>> LogListAsync(string? keyword, string? changeType, int page, int pageSize);
    /// <summary>导出库存流水全量（按 keyword/changeType 筛选，不分页）</summary>
    Task<List<Dictionary<string, object?>>> ExportLogAsync(string? keyword, string? changeType);
}
