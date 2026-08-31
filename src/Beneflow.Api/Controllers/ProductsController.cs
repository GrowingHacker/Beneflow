using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/products")]
public class ProductsController : BaseApiController
{
    private readonly IProductService _svc;
    public ProductsController(IProductService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ListAsync(keyword, page, pageSize));

    [HttpGet("by-barcode/{barcode}")]
    public Task<ApiResult<object?>> ByBarcode(string barcode) => _svc.GetByBarcode(barcode);

    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] ProductUpsertDto dto) => _svc.CreateAsync(dto);

    /// <summary>整体或部分更新；兼容仅 { status } 的上下架开关提交</summary>
    [HttpPut("{id:int}")]
    public Task<ApiResult> Update(int id, [FromBody] JsonElement body) => _svc.UpdateAsync(id, body);

    /// <summary>逻辑删除</summary>
    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);
}
