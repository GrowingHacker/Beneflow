using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

/// <summary>操作日志（不可删除，仅查看）</summary>
[Route("api/v1/logs")]
public class LogsController : BaseApiController
{
    private readonly ILogService _svc;
    public LogsController(ILogService svc) => _svc = svc;

    /// <summary>操作日志列表（按关键词/日期筛选，分页；日志只读不可删除）</summary>
    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] string? dateFrom, [FromQuery] string? dateTo,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        DateTime.TryParse(dateFrom, out var from);
        DateTime.TryParse(dateTo, out var to);
        return ApiResult<PagedResult<object>>.Ok(
            await _svc.QueryAsync(keyword,
                string.IsNullOrEmpty(dateFrom) ? null : from,
                string.IsNullOrEmpty(dateTo) ? null : to,
                page, pageSize));
    }

    // 日志不允许写操作 —— 显式声明防止误用
    [HttpPost] [ApiExplorerSettings(IgnoreApi = true)] [Obsolete] public IActionResult Post() => NotFound();
}
