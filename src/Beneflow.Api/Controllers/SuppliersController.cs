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

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ListAsync(keyword, page, pageSize));

    [HttpGet("{id:int}")]
    public Task<ApiResult<object>> Get(int id) => _svc.GetAsync(id);

    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] SupplierUpsertDto dto) => _svc.CreateAsync(dto);

    /// <summary>整体更新；兼容仅 { status } 的启停开关提交</summary>
    [HttpPut("{id:int}")]
    public Task<ApiResult> Update(int id, [FromBody] JsonElement body) => _svc.UpdateAsync(id, body);

    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);
}
