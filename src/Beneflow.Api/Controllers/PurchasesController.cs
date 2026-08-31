using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/purchases")]
public class PurchasesController : BaseApiController
{
    private readonly IPurchaseService _svc;
    public PurchasesController(IPurchaseService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] string? dateFrom, [FromQuery] string? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ListAsync(keyword, dateFrom, dateTo, page, pageSize));

    [HttpGet("{id:int}")]
    public Task<ApiResult<object>> Detail(int id) => _svc.GetDetailAsync(id);

    /// <summary>创建进货单：入库存 + 移动加权平均成本 + 流水</summary>
    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] CreatePurchaseDto dto) => _svc.CreateAsync(dto);
}
