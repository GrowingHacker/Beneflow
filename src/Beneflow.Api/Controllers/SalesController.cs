using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/sales")]
public class SalesController : BaseApiController
{
    private readonly ISaleService _svc;
    public SalesController(ISaleService svc) => _svc = svc;

    /// <summary>销售单列表（按关键词/日期/收款方式筛选，分页）</summary>
    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] string? dateFrom, [FromQuery] string? dateTo,
        [FromQuery] string? payMethod, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ListAsync(keyword, dateFrom, dateTo, payMethod, page, pageSize));

    /// <summary>销售单详情（含明细行）</summary>
    [HttpGet("{id:int}")]
    public Task<ApiResult<object>> Detail(int id) => _svc.GetDetailAsync(id);

    /// <summary>按单号查询销售单详情（小票补打用）</summary>
    [HttpGet("by-no/{orderNo}")]
    public Task<ApiResult<object>> DetailByNo(string orderNo) => _svc.GetDetailByOrderNoAsync(orderNo);

    /// <summary>收银结算：扣库存、写流水，赊账生成欠款记录</summary>
    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] CreateSaleDto dto) => _svc.CreateAsync(dto);

    /// <summary>作废销售单：仅翻转 IsVoided 标记，不影响库存；已作废订单不计入看板统计</summary>
    [HttpPost("{id:int}/void")]
    public Task<ApiResult> Void(int id) => _svc.VoidAsync(id);
}
