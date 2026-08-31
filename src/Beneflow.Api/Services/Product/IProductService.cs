using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;

namespace Beneflow.Api.Services;

/// <summary>商品档案与分类</summary>
public interface IProductService
{
    Task<PagedResult<object>> ListAsync(string? keyword, int page, int pageSize);
    Task<ApiResult<object?>> GetByBarcode(string barcode);
    Task<ApiResult<object>> CreateAsync(ProductUpsertDto dto);
    Task<ApiResult> UpdateAsync(int id, JsonElement body);
    Task<ApiResult> DeleteAsync(int id);
    Task<List<ProductCategory>> CategoriesAsync();
    Task<ApiResult<object>> CreateCategoryAsync(string name, int? parentId);
    Task<ApiResult> DeleteCategoryAsync(int id);
}
