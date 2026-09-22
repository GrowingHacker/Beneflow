using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/purchase-returns")]
public class PurchaseReturnsController : BaseApiController
{
    private readonly IPurchaseService _svc;
    public PurchaseReturnsController(IPurchaseService svc) => _svc = svc;

    /// <summary>采购退货单列表（按关键词筛选，分页）</summary>
    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ReturnListAsync(keyword, page, pageSize));

    /// <summary>创建采购退货单：回退库存并写库存流水</summary>
    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] CreatePurchaseReturnDto dto) => _svc.CreateReturnAsync(dto);

    /// <summary>作废采购退货单：回补库存并标记作废（已作废的不重复处理）</summary>
    [HttpPost("{id:int}/void")]
    public Task<ApiResult> Void(int id) => _svc.VoidReturnAsync(id);
}
