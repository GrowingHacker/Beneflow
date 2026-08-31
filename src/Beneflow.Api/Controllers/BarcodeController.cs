using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

/// <summary>条码联网反查（本地商品库没有时，从公开数据源拉商品名/分类/规格/建议价）。</summary>
[Route("api/v1/barcode")]
public class BarcodeController : BaseApiController
{
    private readonly IBarcodeService _svc;
    public BarcodeController(IBarcodeService svc) => _svc = svc;

    /// <summary>
    /// 按条码联网反查商品信息。
    /// 结果：有数据则返回 info（name/category/spec/unit/suggestedSalePrice/brand/source），
    /// 没有命中返回 code=0, data=null（前端据此决定是否弹空白新建表单）。
    /// </summary>
    [HttpGet("lookup/{code}")]
    public async Task<ApiResult<BarcodeInfo?>> Lookup(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length < 6)
            return ApiResult<BarcodeInfo?>.Fail("条码长度不足");
        var info = await _svc.LookupAsync(code.Trim());
        return ApiResult<BarcodeInfo?>.Ok(info);
    }
}
