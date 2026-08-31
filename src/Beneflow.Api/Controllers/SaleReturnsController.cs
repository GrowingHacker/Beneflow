using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/sale-returns")]
public class SaleReturnsController : BaseApiController
{
    private readonly ISaleService _svc;
    public SaleReturnsController(ISaleService svc) => _svc = svc;

    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ReturnListAsync(keyword, page, pageSize));

    /// <summary>销售退货：核销可退数量 → 回补库存 → 写流水</summary>
    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] CreateSaleReturnDto dto) => _svc.CreateReturnAsync(dto);
}
