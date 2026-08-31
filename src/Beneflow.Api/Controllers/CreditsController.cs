using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/credits")]
public class CreditsController : BaseApiController
{
    private readonly ISaleService _svc;
    public CreditsController(ISaleService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<object>> List(
        [FromQuery] string? wechatId, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<object>.Ok(await _svc.CreditListAsync(wechatId, status, page, pageSize));

    /// <summary>结清赊账：写入还款记录并标记已结清</summary>
    [HttpPost("{id:int}/settle")]
    public Task<ApiResult> Settle(int id, [FromBody] SettleCreditDto body) => _svc.SettleAsync(id, body ?? new SettleCreditDto(), null!);
}
