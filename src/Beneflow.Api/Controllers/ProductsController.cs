using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

[Route("api/v1/products")]
public class ProductsController : BaseApiController
{
    private readonly IProductService _svc;
    public ProductsController(IProductService svc) => _svc = svc;

    /// <summary>商品列表（按关键词搜索，分页）</summary>
    [HttpGet]
    public async Task<ApiResult<PagedResult<object>>> List(
        [FromQuery] string? keyword, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        => ApiResult<PagedResult<object>>.Ok(await _svc.ListAsync(keyword, page, pageSize));

    /// <summary>按条码查询商品（收银台扫码用）；未找到时 data 为 null</summary>
    [HttpGet("by-barcode/{barcode}")]
    public Task<ApiResult<object?>> ByBarcode(string barcode) => _svc.GetByBarcode(barcode);

    /// <summary>获取称重商品列表（收银台快捷面板用）</summary>
    [HttpGet("weighted")]
    public async Task<ApiResult<List<object>>> Weighted()
        => ApiResult<List<object>>.Ok(await _svc.GetWeightedProductsAsync());

    /// <summary>新增商品</summary>
    [HttpPost]
    public Task<ApiResult<object>> Create([FromBody] ProductUpsertDto dto) => _svc.CreateAsync(dto);

    /// <summary>整体或部分更新；兼容仅 { status } 的上下架开关提交</summary>
    [HttpPut("{id:int}")]
    public Task<ApiResult> Update(int id, [FromBody] JsonElement body) => _svc.UpdateAsync(id, body);

    /// <summary>逻辑删除</summary>
    [HttpDelete("{id:int}")]
    public Task<ApiResult> Delete(int id) => _svc.DeleteAsync(id);

    /// <summary>
    /// 解析商品导入 Excel，返回表头、列映射与逐行校验结果（不写库）。
    /// mapping 为可选的列映射覆盖（JSON：字段 key → 表头文字），用于修正自动识别错的列。
    /// </summary>
    [HttpPost("import/preview")]
    public Task<ApiResult<object>> ImportPreview(IFormFile file, [FromForm] string? mapping)
        => _svc.ParseImportAsync(file, mapping);

    /// <summary>批量导入商品档案：整批一个事务，入参为预览页确认后的行数据</summary>
    [HttpPost("import/batch")]
    public Task<ApiResult<object>> ImportBatch([FromBody] ProductImportDto dto) => _svc.ImportBatchAsync(dto);

    /// <summary>下载商品导入模板 .xlsx</summary>
    [HttpGet("import/template")]
    public IActionResult ImportTemplate()
    {
        var bytes = _svc.BuildImportTemplate();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "商品导入模板.xlsx");
    }
}
