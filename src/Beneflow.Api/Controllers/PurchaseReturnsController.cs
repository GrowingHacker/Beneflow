using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/purchase-returns")]
public class PurchaseReturnsController : BaseApiController
{
    private readonly IPurchaseService _svc;
    public PurchaseReturnsController(IPurchaseService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ReturnListAsync(keyword, page, pageSize));

    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] CreatePurchaseReturnDto dto) => _svc.CreateReturnAsync(dto);
}
