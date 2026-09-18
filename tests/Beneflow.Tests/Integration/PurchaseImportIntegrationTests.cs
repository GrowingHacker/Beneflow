using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClosedXML.Excel;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 进货单导入的 HTTP 契约测试 + 多步流程测试。
///
/// 覆盖两层：
/// 1. 契约与回归点：未登录 401、非 Excel 扩展名给中文失败、不可读文件不再 500、模板可下载；
/// 2. 多步流程：Sheet 名对不上供应商不报错而是回报名单；以及
///    「上传预览 → 按前端映射批量建单 → 跨端点回查库存」这条真实链路。
///
/// 解析规则本身（同义词表头、行级分流、条码/名称匹配）在 `Unit/PurchaseImportTests.cs`。
/// </summary>
public class PurchaseImportIntegrationTests : IntegrationTestBase
{
    public PurchaseImportIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>种子供应商（DbSeeder）与其中一个种子商品的条码</summary>
    private const string SeedSupplier = "华南批发商行";
    private const string SeedBarcode = "6920202888823";   // 可口可乐 330ml

    // ========== 构造入参 ==========

    /// <summary>按前端提交方式构造 multipart：字段名必须是 file（控制器签名 IFormFile file）</summary>
    private static MultipartFormDataContent Multipart(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(XlsxMime);
        content.Add(part, "file", fileName);
        return content;
    }

    /// <summary>造一张真实 .xlsx：Sheet 名 = 供应商名</summary>
    private static byte[] XlsxFor(string sheetName, string[] headers, params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = rows[r][c];

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>通过公开接口读商品实时库存（跨端点回查，不直接碰 DbContext）</summary>
    private async Task<decimal> StockOf(string barcode)
    {
        var data = await ExpectOk(await Client.GetAsync($"/api/v1/products/by-barcode/{barcode}"));
        return data.GetProperty("stockQuantity").GetDecimal();
    }

    // ========== 契约与回归点 ==========

    [Fact]
    public async Task ImportPreview_WithoutToken_Returns401()
    {
        var resp = await Client.PostAsync("/api/v1/purchases/import/preview",
            Multipart(Encoding.UTF8.GetBytes("x"), "x.xlsx"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ImportPreview_NonExcelExtension_ReturnsBusinessFail()
    {
        await LoginAsAdminAsync();
        var resp = await Client.PostAsync("/api/v1/purchases/import/preview",
            Multipart(Encoding.UTF8.GetBytes("a,b\n1,2"), "进货单.csv"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadBody(resp);
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains(".xlsx", body.GetProperty("message").GetString()!);
    }

    [Theory]
    // 改名/损坏文件（伪装成 .xlsx 的纯文本）
    [InlineData(".xlsx")]
    // 旧版 .xls（BIFF/OLE2 签名）—— 扩展名放行但 ClosedXML 读不了
    [InlineData(".xls")]
    public async Task ImportPreview_UnreadableFile_ReturnsChineseFailNot500(string ext)
    {
        await LoginAsAdminAsync();
        var bytes = ext == ".xls"
            ? new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }
            : Encoding.UTF8.GetBytes("这不是一个 Excel 文件");

        var resp = await Client.PostAsync("/api/v1/purchases/import/preview",
            Multipart(bytes, "坏文件" + ext));

        // 回归点：原先这里未兜住 ClosedXML 的异常，会冒到全局兜底变成 500「服务器内部错误」
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadBody(resp);
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.NotEqual("服务器内部错误", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ImportTemplate_DownloadsReadableXlsxWithGuideSheet()
    {
        await LoginAsAdminAsync();
        var resp = await Client.GetAsync("/api/v1/purchases/import/template");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(XlsxMime, resp.Content.Headers.ContentType?.MediaType);

        // 下载下来的东西必须真的是 ClosedXML 打得开的 .xlsx（内容与声明一致）
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(2, wb.Worksheets.Count);          // 使用说明 + 示例供应商
        Assert.NotNull(wb.Worksheet("使用说明"));
    }

    // ========== 多步流程 ==========

    [Fact]
    public async Task ImportPreview_SheetNameNotMatchingSupplier_ReportsItWithoutFailing()
    {
        await LoginAsAdminAsync();
        var resp = await Client.PostAsync("/api/v1/purchases/import/preview",
            Multipart(XlsxFor("这个供应商不存在", new[] { "条码", "商品名称", "数量", "进价" },
                new[] { SeedBarcode, "可口可乐 330ml", "1", "1.60" }), "进货单.xlsx"));

        // 供应商没匹配上属于「可引导」的状态，不是错误：前端要拿名单去让用户建供应商
        var data = await ExpectOk(resp);
        Assert.Equal(0, data.GetProperty("orders").GetArrayLength());

        var sheets = data.GetProperty("unmatchedSheets");
        Assert.Equal(1, sheets.GetArrayLength());
        Assert.Equal("这个供应商不存在", sheets[0].GetString());
    }

    [Fact]
    public async Task ImportMultiStep_PreviewThenBatch_StockReallyIncreases()
    {
        await LoginAsAdminAsync();
        var before = await StockOf(SeedBarcode);

        // 1) 上传预览 —— 只解析不写库
        var previewResp = await Client.PostAsync("/api/v1/purchases/import/preview",
            Multipart(XlsxFor(SeedSupplier, new[] { "条码", "商品名称", "数量", "进价" },
                new[] { SeedBarcode, "可口可乐 330ml", "10", "1.60" }), "进货单.xlsx"));

        var data = await ExpectOk(previewResp);
        var orders = data.GetProperty("orders");
        Assert.Equal(1, orders.GetArrayLength());

        var order = orders[0];
        Assert.Equal(SeedSupplier, order.GetProperty("supplierName").GetString());
        Assert.Equal(10m, order.GetProperty("totalQty").GetDecimal());
        Assert.Equal(16m, order.GetProperty("totalAmount").GetDecimal());

        var items = order.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(SeedBarcode, items[0].GetProperty("barcode").GetString());

        // 预览阶段不能动库存
        Assert.Equal(before, await StockOf(SeedBarcode));

        // 2) 这就是前端 confirmImport 的映射：orders[].items[] → details[]，再 POST /purchases/batch
        var item = items[0];
        var payload = new[]
        {
            new
            {
                supplierId = order.GetProperty("supplierId").GetInt32(),
                details = new[]
                {
                    new
                    {
                        productId = item.GetProperty("productId").GetInt32(),
                        name = item.GetProperty("name").GetString(),
                        qty = item.GetProperty("qty").GetDecimal(),
                        costPrice = item.GetProperty("costPrice").GetDecimal(),
                        produceDate = (string?)null,
                    },
                },
            },
        };

        var batch = await ExpectOk(await PostJsonAsync("/api/v1/purchases/batch", payload));
        Assert.Equal(1, batch.GetProperty("total").GetInt32());
        Assert.Equal(1, batch.GetProperty("created").GetArrayLength());
        Assert.Equal(0, batch.GetProperty("failed").GetArrayLength());

        // 3) 跨端点回查：预览里显示的数量真的变成了库存
        Assert.Equal(before + 10, await StockOf(SeedBarcode));
    }
}
