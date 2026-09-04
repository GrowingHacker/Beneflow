using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>销售管理：收银结算 + 赊账 + 销售退货</summary>
public interface ISaleService
{
    Task<PagedResult<object>> ListAsync(string? keyword, string? dateFrom, string? dateTo, string? payMethod, int page, int pageSize);
    Task<ApiResult<object>> GetDetailAsync(int id);
    Task<ApiResult<object>> GetDetailByOrderNoAsync(string orderNo);
    Task<ApiResult<object>> CreateAsync(CreateSaleDto dto);
    Task<PagedResult<object>> ReturnListAsync(string? keyword, int page, int pageSize);
    Task<ApiResult<object>> CreateReturnAsync(CreateSaleReturnDto dto);
    Task<object> CreditListAsync(string? keyword, string? status, string? dateFrom, string? dateTo, int page, int pageSize);
    /// <summary>导出赊账记录全量（按 keyword/status/dateFrom/dateTo 筛选，不分页）</summary>
    Task<List<Dictionary<string, object?>>> ExportCreditAsync(string? keyword, string? status, string? dateFrom, string? dateTo);
    Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword, string? dateFrom, string? dateTo, string? payMethod);
    Task<ApiResult> SettleAsync(int id, SettleCreditDto dto, string ip);
    Task<ApiResult> UpdateCreditAsync(int id, UpdateCreditDto dto);
}
