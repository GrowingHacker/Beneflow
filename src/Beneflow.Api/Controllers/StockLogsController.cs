using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

/// <summary>库存流水查询</summary>
[Route("api/v1/stock-logs")]
public class StockLogsController : BaseApiController
{
    private readonly IStockService _svc;
    public StockLogsController(IStockService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] string? changeType,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.LogListAsync(keyword, changeType, page, pageSize));
}
