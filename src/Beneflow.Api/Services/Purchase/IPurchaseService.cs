using Beneflow.Api.Models;
using Microsoft.AspNetCore.Http;

namespace Beneflow.Api.Services;

/// <summary>采购管理：进货单（移动加权平均成本）+ 采购退货</summary>
public interface IPurchaseService
{
    Task<PagedResult<object>> ListAsync(string? keyword, string? dateFrom, string? dateTo, int page, int pageSize);
    Task<ApiResult<object>> GetDetailAsync(int id);
    Task<ApiResult<object>> CreateAsync(CreatePurchaseDto dto);
    /// <summary>作废进货单：回退库存、删除批次、标记作废</summary>
    Task<ApiResult> VoidAsync(int id);
    /// <summary>编辑进货单：作废旧单 + 新增新单（复用 CreateAsync 逻辑），支持修改数量、进价、备注</summary>
    Task<ApiResult<object>> UpdateAsync(int id, CreatePurchaseDto dto);
    Task<PagedResult<object>> ReturnListAsync(string? keyword, int page, int pageSize);
    Task<ApiResult<object>> CreateReturnAsync(CreatePurchaseReturnDto dto);
    /// <summary>进货单全量导出（按 keyword/dateFrom/dateTo 筛选，不分页）</summary>
    Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword, string? dateFrom, string? dateTo);

    /// <summary>解析进货单导入 Excel，返回预览明细和错误（不写库）</summary>
    Task<ApiResult<object>> ParseImportAsync(IFormFile file);

    /// <summary>批量创建进货单：逐张调用 CreateAsync（各自独立事务），返回成功/失败明细</summary>
    Task<ApiResult<object>> CreateBatchAsync(List<CreatePurchaseDto> dtos);

    /// <summary>生成进货单导入模板 .xlsx</summary>
    byte[] BuildImportTemplate();
}
