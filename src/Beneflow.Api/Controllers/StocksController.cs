using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/stocks")]
public class StocksController : BaseApiController
{
    private readonly IStockService _svc;
    public StocksController(IStockService svc) => _svc = svc;

    /// <summary>实时库存（status 可选：缺货/预警/正常/临期/过期）</summary>
    [HttpGet]
    public async Task<ApiResult<object>> List(
        [FromQuery] string? keyword, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<object>.Ok(await _svc.InventoryListAsync(keyword, status, page, pageSize));

    // ---------- 盘点 ----------
    [HttpGet("check")]
    public async Task<ApiResult<PagedResult<object>>> CheckList(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.CheckListAsync(page, pageSize));

    /// <summary>创建盘点单（status=草稿 或 已确认）</summary>
    [HttpPost("check")]
    public Task<ApiResult<object>> CreateCheck([FromBody] CreateStockCheckDto dto) => _svc.CreateCheckAsync(dto);

    /// <summary>确认盘点：按差异调整库存并写流水</summary>
    [HttpPost("check/{id:int}/confirm")]
    public Task<ApiResult> ConfirmCheck(int id) => _svc.ConfirmCheckAsync(id);

    // ---------- 临期商品 ----------
    /// <summary>临期批次列表（tag：7 / 30 / expired / processed）</summary>
    [HttpGet("expiry")]
    public async Task<ApiResult<object>> Expiry(
        [FromQuery] string? tag, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => ApiResult<object>.Ok(await _svc.ExpiryListAsync(tag, page, pageSize));

    /// <summary>批量标记临期已处理</summary>
    [HttpPost("expiry/mark-processed")]
    public async Task<ApiResult> MarkProcessed([FromBody] long[] ids) => await _svc.MarkProcessedAsync(ids);

    // ---------- 库存预警 ----------
    /// <summary>低于安全库存的商品 + 建议补货数量</summary>
    [HttpGet("warnings")]
    public async Task<ApiResult<List<object>>> Warnings() =>
        ApiResult<List<object>>.Ok(await _svc.WarningsAsync());
}
