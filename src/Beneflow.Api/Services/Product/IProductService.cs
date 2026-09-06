using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;

namespace Beneflow.Api.Services;

/// <summary>商品档案与分类</summary>
public interface IProductService
{
    Task<PagedResult<object>> ListAsync(string? keyword, int page, int pageSize);
    /// <summary>导出全量（按 keyword 筛选，不分页），返回行字典</summary>
    Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword);
    Task<ApiResult<object?>> GetByBarcode(string barcode);
    /// <summary>获取称重商品列表（收银台快捷面板用）</summary>
    Task<List<object>> GetWeightedProductsAsync();
    Task<ApiResult<object>> CreateAsync(ProductUpsertDto dto);
    Task<ApiResult> UpdateAsync(int id, JsonElement body);
    Task<ApiResult> DeleteAsync(int id);
    Task<List<ProductCategory>> CategoriesAsync();
    Task<ApiResult<object>> CreateCategoryAsync(string name, int? parentId);
    Task<ApiResult> DeleteCategoryAsync(int id);
}
