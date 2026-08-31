using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/sales")]
public class SalesController : BaseApiController
{
    private readonly ISaleService _svc;
    public SalesController(ISaleService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] string? dateFrom, [FromQuery] string? dateTo,
        [FromQuery] string? payMethod, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ListAsync(keyword, dateFrom, dateTo, payMethod, page, pageSize));

    [HttpGet("{id:int}")]
    public Task<ApiResult<object>> Detail(int id) => _svc.GetDetailAsync(id);

    [HttpGet("by-no/{orderNo}")]
    public Task<ApiResult<object>> DetailByNo(string orderNo) => _svc.GetDetailByOrderNoAsync(orderNo);

    /// <summary>收银结算：扣库存、写流水，赊账生成欠款记录</summary>
    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] CreateSaleDto dto) => _svc.CreateAsync(dto);
}
