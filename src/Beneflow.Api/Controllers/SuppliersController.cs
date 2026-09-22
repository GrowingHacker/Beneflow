using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/suppliers")]
public class SuppliersController : BaseApiController
{
    private readonly ISupplierService _svc;
    public SuppliersController(ISupplierService svc) => _svc = svc;

    /// <summary>供应商列表（按关键词搜索，分页）</summary>
    [HttpGet]
    public async Task<ApiResult<PagedResult<SupplierListItemDto>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<SupplierListItemDto>>.Ok(await _svc.ListAsync(keyword, page, pageSize));

    /// <summary>供应商详情</summary>
    [HttpGet("{id:int}")]
    public Task<ApiResult<SupplierDetailDto>> Get(int id) => _svc.GetAsync(id);

    /// <summary>新增供应商</summary>
    [HttpPost]
    public Task<ApiResult<IdResultDto>> Create([FromBody] SupplierUpsertDto dto) => _svc.CreateAsync(dto);

    /// <summary>整体更新；兼容仅 { status } 的启停开关提交</summary>
    [HttpPut("{id:int}")]
    public Task<ApiResult> Update(int id, [FromBody] JsonElement body) => _svc.UpdateAsync(id, body);

    /// <summary>删除供应商</summary>
    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);
}
