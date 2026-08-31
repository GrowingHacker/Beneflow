using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/reports")]
public class ReportsController : BaseApiController
{
    private readonly IReportService _svc;
    public ReportsController(IReportService svc) => _svc = svc;

    /// <summary>日销售报表（date 默认今天）</summary>
    [HttpGet("daily-sales")]
    public async Task<ApiResult<object>> DailySales([FromQuery] string? date)
    {
        var d = DateTime.TryParse(date, out var parsed) ? parsed.Date : DateTime.Today;
        return ApiResult<object>.Ok(await _svc.DailySalesAsync(d));
    }

    /// <summary>月销售报表（month 格式 yyyy-MM）</summary>
    [HttpGet("monthly-sales")]
    public async Task<ApiResult<object>> MonthlySales([FromQuery] string? month)
    {
        var (y, m) = ParseMonth(month);
        return ApiResult<object>.Ok(await _svc.MonthlySalesAsync(y, m));
    }

    [HttpGet("profit")]
    public async Task<ApiResult<object>> Profit([FromQuery] string? dateFrom, [FromQuery] string? dateTo)
    {
        var f = DateTime.TryParse(dateFrom, out var pf) ? pf.Date : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var t = DateTime.TryParse(dateTo, out var pt) ? pt.Date : DateTime.Today;
        return ApiResult<object>.Ok(await _svc.ProfitAnalysisAsync(f, t));
    }

    /// <summary>供应商对账单</summary>
    [HttpGet("supplier-statement")]
    public async Task<ApiResult<object>> SupplierStatement(
        [FromQuery] int supplierId, [FromQuery] string? dateFrom, [FromQuery] string? dateTo)
        => ApiResult<object>.Ok(await _svc.SupplierStatementAsync(supplierId, dateFrom, dateTo));

    /// <summary>赊账汇总</summary>
    [HttpGet("credit-summary")]
    public async Task<ApiResult<object>> CreditSummary([FromQuery] string? dateFrom, [FromQuery] string? dateTo)
        => ApiResult<object>.Ok(await _svc.CreditSummaryAsync(dateFrom, dateTo));

    private static (int y, int m) ParseMonth(string? month)
    {
        if (!string.IsNullOrEmpty(month))
        {
            var parts = month.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var py) && int.TryParse(parts[1], out var pm)
                && pm is >= 1 and <= 12 && py is >= 2000 and <= 2100)
                return (py, pm);
        }
        return (DateTime.Today.Year, DateTime.Today.Month);
    }
}
