using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/categories")]
public class CategoriesController : BaseApiController
{
    private readonly IProductService _svc;
    public CategoriesController(IProductService svc) => _svc = svc;

    /// <summary>商品分类列表（扁平返回，含 ParentId 供前端组树）</summary>
    [HttpGet]
    public async Task<ApiResult<List<ProductCategory>>> List() =>
        ApiResult<List<ProductCategory>>.Ok(await _svc.CategoriesAsync());

    /// <summary>新增商品分类；名称必填，可指定父分类</summary>
    [HttpPost]
    public async Task<ApiResult<object>> Create([FromBody] CategoryBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name))
            return ApiResult<object>.Fail("分类名称不能为空");
        return await _svc.CreateCategoryAsync(b.Name.Trim(), b.ParentId);
    }

    /// <summary>删除商品分类</summary>
    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteCategoryAsync(id);

    public class CategoryBody { public string? Name { get; set; } public int? ParentId { get; set; } }
}
