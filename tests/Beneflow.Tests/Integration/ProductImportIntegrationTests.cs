using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 商品档案导入的 HTTP 契约集成测试。
///
/// 与 <c>Unit/ProductImportTests.cs</c> 的分工：那 22 条把真实 xlsx 直接喂给 Service，
/// 覆盖的是解析/校验/落库逻辑（复杂度所在）；这里只补它们结构上看不到的那一层 ——
/// 路由绑定、multipart 字段名、鉴权、序列化后的响应形状、以及跨端点是否真的落了库。
///
/// 之所以值得单独测：前端用 <c>fd.append('file')</c> / <c>fd.append('mapping')</c> 提交，
/// 控制器用 <c>IFormFile file</c> + <c>[FromForm] string? mapping</c> 接收。任一端改名，
/// Service 单测会全绿而功能整体失效 —— 只有这一层能发现。
/// </summary>
public class ProductImportIntegrationTests : IntegrationTestBase
{
    public ProductImportIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static string NewTag() => Guid.NewGuid().ToString("N")[..8];

    // ================= 构造真实 xlsx（导入走 ClosedXML，不能塞假字节）=================

    private static byte[] NewXlsx(string[] headers, params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("商品档案");
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = rows[r][c];
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 按前端的提交方式构造 multipart：字段名必须是 file / mapping，
    /// 这里的字面量就是被测的契约本身，故意不用常量间接引用。
    /// </summary>
    private static MultipartFormDataContent Multipart(byte[] bytes, string fileName = "商品导入.xlsx", string? mapping = null)
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(XlsxMime);
        content.Add(part, "file", fileName);
        if (mapping != null) content.Add(new StringContent(mapping, Encoding.UTF8), "mapping");
        return content;
    }

    /// <summary>批量导入的单行请求体（字段名与 ProductImportRowDto 的 camelCase 契约一致）</summary>
    private static object RowBody(string barcode, string name, decimal salePrice = 5m, decimal stock = 7m) => new
    {
        row = 2,
        barcode,
        name,
        categoryName = "集成测试分类",
        unit = "瓶",
        spec = "500ml",
        salePrice,
        costPrice = 3m,
        stockQuantity = stock,
        safetyStock = 0m,
        shelfLifeDays = 0,
        isWeighted = false,
        status = true,
        remark = "IT",
    };

    // ================= 1. 鉴权 =================

    [Fact]
    public async Task ImportEndpoints_WithoutToken_Return401()
    {
        // 不登录：三个端点都应在授权阶段被拦下（依赖 BaseApiController 的 [Authorize]）。
        // Service 单测绕开全部过滤器，「未登录能不能导」这类问题只有这里回答得了。
        var preview = await Client.PostAsync("/api/v1/products/import/preview",
            Multipart(NewXlsx(new[] { "商品名称" }, new[] { "N" })));
        Assert.Equal(HttpStatusCode.Unauthorized, preview.StatusCode);

        var batch = await Client.PostAsync("/api/v1/products/import/batch", null);
        Assert.Equal(HttpStatusCode.Unauthorized, batch.StatusCode);

        var template = await Client.GetAsync("/api/v1/products/import/template");
        Assert.Equal(HttpStatusCode.Unauthorized, template.StatusCode);
    }

    // ================= 2. 模板下载 =================

    [Fact]
    public async Task ImportTemplate_ReturnsReadableXlsxWithUtf8Filename()
    {
        await LoginAsAdminAsync();
        var resp = await Client.GetAsync("/api/v1/products/import/template");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(XlsxMime, resp.Content.Headers.ContentType?.MediaType);

        // 中文文件名必须由 Content-Disposition 的 filename* 承载（RFC 5987 百分号编码），
        // 否则落到浏览器就是乱码文件名 —— 单测只能验 byte[]，看不到这一段。
        var disposition = resp.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        var star = disposition!.FileNameStar;
        Assert.False(string.IsNullOrWhiteSpace(star), "中文文件名应写在 filename* 里");
        var raw = star!.Trim('"');
        var sep = raw.LastIndexOf("''", StringComparison.Ordinal);   // 形如 UTF-8''%E5%95%86...
        Assert.Equal("商品导入模板.xlsx", Uri.UnescapeDataString(sep >= 0 ? raw[(sep + 2)..] : raw));

        // 内容得是真能打开的 xlsx，防止模板退化成占位字节
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var sheetNames = wb.Worksheets.Select(w => w.Name).ToList();
        Assert.Contains("使用说明", sheetNames);
        Assert.Contains("商品档案", sheetNames);
    }

    // ================= 3. multipart 绑定 + 列映射覆盖（最有价值的一条）=================

    [Fact]
    public async Task ImportPreview_Multipart_BindsFileAndHonoursMappingOverride()
    {
        await LoginAsAdminAsync();
        var tag = NewTag();
        // 表头故意全用「别人家的叫法」，走同义词识别
        var xlsx = NewXlsx(
            new[] { "条形码", "品名", "售价" },
            new[] { "IT" + tag, "集成测试-映射-" + tag, "3.5" });

        // ① 只传 file：后端应自行识别出三列
        var auto = await ExpectOk(await Client.PostAsync("/api/v1/products/import/preview", Multipart(xlsx)));
        var autoMap = auto.GetProperty("mapping");
        Assert.Equal("条形码", autoMap.GetProperty("barcode").GetString());
        Assert.Equal("品名", autoMap.GetProperty("name").GetString());
        Assert.Equal("售价", autoMap.GetProperty("salePrice").GetString());
        Assert.Equal(3.5m, auto.GetProperty("rows")[0].GetProperty("salePrice").GetDecimal());

        // ② 传 mapping 把「售价」显式清空 ⇒ 空串表示「这一列不要」，且不允许自动识别补回。
        // 这条同时锁住了「mapping 这个表单字段真的被控制器收到了」和「空串语义」两件事。
        var cleared = await ExpectOk(await Client.PostAsync("/api/v1/products/import/preview",
            Multipart(xlsx, mapping: $"{{\"barcode\":\"条形码\",\"name\":\"品名\",\"salePrice\":\"\"}}")));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("mapping").GetProperty("salePrice").ValueKind);
        Assert.Equal(0m, cleared.GetProperty("rows")[0].GetProperty("salePrice").GetDecimal());

        // 其余列不受影响，证明是「精准清空」而不是整个 mapping 被丢弃
        Assert.Equal("条形码", cleared.GetProperty("mapping").GetProperty("barcode").GetString());
        Assert.Equal("品名", cleared.GetProperty("mapping").GetProperty("name").GetString());
    }

    // ================= 4. 落库端到端（跨端点回查）=================

    [Fact]
    public async Task ImportBatch_ThenProductAppearsInList()
    {
        await LoginAsAdminAsync();
        var tag = NewTag();
        var barcode = "IT" + tag;
        var name = "集成测试商品-" + tag;

        var data = await ExpectOk(await PostJsonAsync("/api/v1/products/import/batch", new
        {
            rows = new[] { RowBody(barcode, name, salePrice: 6.50m, stock: 12m) },
            duplicatePolicy = "skip",
        }));
        Assert.Equal(1, data.GetProperty("created").GetInt32());
        Assert.Empty(data.GetProperty("failed").EnumerateArray());

        // 不只看返回值，而是换一个端点确认真的落库了
        var list = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        var item = list.GetProperty("data").GetProperty("list").EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == name);
        Assert.Equal(barcode, item.GetProperty("barcode").GetString());
        Assert.Equal(6.50m, item.GetProperty("salePrice").GetDecimal());
        Assert.Equal(12m, item.GetProperty("stockQuantity").GetDecimal());
    }

    // ================= 5. duplicatePolicy 缺省值 =================

    [Fact]
    public async Task ImportBatch_WithoutDuplicatePolicy_DefaultsToSkip()
    {
        await LoginAsAdminAsync();
        var tag = NewTag();
        var barcode = "IT" + tag;
        var name = "集成测试重复条码-" + tag;

        // policy 为 null 时不把 duplicatePolicy 放进请求体，验证 DTO 上的默认值
        // 能穿过 JSON 反序列化（若默认值失效，第二次导入会重复建商品而不是跳过）
        async Task<JsonElement> Import(string? policy)
        {
            var body = new Dictionary<string, object>
            {
                ["rows"] = new[] { RowBody(barcode, name) },
            };
            if (policy != null) body["duplicatePolicy"] = policy;
            return await ExpectOk(await PostJsonAsync("/api/v1/products/import/batch", body));
        }

        Assert.Equal(1, (await Import("skip")).GetProperty("created").GetInt32());

        var second = await Import(null);
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(1, second.GetProperty("skipped").GetInt32());
    }

    // ================= 6~7. 坏输入必须是中文业务失败，而不是 500 =================

    [Fact]
    public async Task ImportPreview_NonExcelExtension_ReturnsBusinessFail()
    {
        await LoginAsAdminAsync();
        var resp = await Client.PostAsync("/api/v1/products/import/preview",
            Multipart(Encoding.UTF8.GetBytes("a,b\n1,2"), fileName: "商品.csv"));

        // 业务失败的契约：HTTP 200 + code != 0 + 中文 message
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

        var resp = await Client.PostAsync("/api/v1/products/import/preview",
            Multipart(bytes, fileName: "坏文件" + ext));

        // 关键：不能被全局异常兜底吞成 500「服务器内部错误」。
        // 用户从旧软件导出 .xls 是很常见的操作，必须给「另存为 .xlsx」这种可操作提示。
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadBody(resp);
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.NotEqual("服务器内部错误", body.GetProperty("message").GetString());
    }
}
