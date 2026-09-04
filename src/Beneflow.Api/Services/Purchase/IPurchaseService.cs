using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>采购管理：进货单（移动加权平均成本）+ 采购退货</summary>
public interface IPurchaseService
{
    Task<PagedResult<object>> ListAsync(string? keyword, string? dateFrom, string? dateTo, int page, int pageSize);
    Task<ApiResult<object>> GetDetailAsync(int id);
    Task<ApiResult<object>> CreateAsync(CreatePurchaseDto dto);
    Task<PagedResult<object>> ReturnListAsync(string? keyword, int page, int pageSize);
    Task<ApiResult<object>> CreateReturnAsync(CreatePurchaseReturnDto dto);
    /// <summary>进货单全量导出（按 keyword/dateFrom/dateTo 筛选，不分页）</summary>
    Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword, string? dateFrom, string? dateTo);
}
