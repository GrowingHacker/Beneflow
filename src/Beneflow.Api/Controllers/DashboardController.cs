using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Beneflow.Api.Utils;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/dashboard")]
public class DashboardController : BaseApiController
{
    private readonly IReportService _reports;
    private readonly ITlsCertService _tls;
    public DashboardController(IReportService reports, ITlsCertService tls)
    { _reports = reports; _tls = tls; }

    [HttpGet("summary")]
    public async Task<ApiResult<object>> Summary()
    {
        var expiryDays = await _reports.GetExpiryDaysAsync();
        return ApiResult<object>.Ok(await _reports.DashboardSummaryAsync(expiryDays));
    }

    [HttpGet("mobile-url")]
    public ApiResult<object> MobileUrl()
    {
        // 与 TLS 证书 SAN 同源的网卡选择逻辑；https 就绪时优先返回 5001 端口
        var ip = NetUtil.LanIPv4s().FirstOrDefault();
        if (ip == null)
            return ApiResult<object>.Fail("未找到局域网网卡地址");

        object payload;
        if (_tls.Ready)
            payload = new
            {
                url = $"https://{ip}:5001/mobile/purchase.html",
                httpUrl = $"http://{ip}:{Request.Host.Port ?? 5000}/mobile/purchase.html",
                lanIp = ip.ToString(),
                https = true,
            };
        else
            payload = new
            {
                url = $"http://{ip}:{Request.Host.Port ?? 5000}/mobile/purchase.html",
                httpUrl = (string?)null,
                lanIp = ip.ToString(),
                https = false,
            };
        return ApiResult<object>.Ok(payload);
    }
}
