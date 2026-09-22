using System.Net;
using ClosedXML.Excel;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 导出（<c>/api/v1/export/*</c>，9 个端点）的上线前集成测试。
///
/// 这一组守的是一条**会静默失败**的路径：<c>ExportController</c> 用反射从 Service 返回的
/// 匿名对象上按**小写属性名**取数据（<c>GetProperty("items")</c>、<c>GetProperty("top5")</c>、
/// <c>Props()</c>）。只要有人把对应的 Service 改成强类型 DTO（属性变大写 <c>Items</c>），
/// 反射就返回 null ⇒ **导出退化成一张只有表头的空表，HTTP 还是 200**，前后端都不报错。
/// 单测只验 byte[]，看不到这一层。
///
/// 所以这里的断言重点是「表里到底有没有数据行」「行里有没有该有的值」，
/// 而不只是「状态码是不是 200」。
/// </summary>
public class ExportIntegrationTests : IntegrationSeedBase
{
    public ExportIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private const string XlsxMime =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static string TodayText => DateTime.Today.ToString("yyyy-MM-dd");

    /// <summary>九个导出端点：路径、额外查询串、期望的工作表名、期望的首列表头</summary>
    public static IEnumerable<object[]> Endpoints()
    {
        yield return new object[] { "/api/v1/export/purchases", "", "采购进货单", "单号" };
        yield return new object[] { "/api/v1/export/sales", "", "销售单列表", "单号" };
        yield return new object[] { "/api/v1/export/products", "", "商品列表", "条码" };
        yield return new object[] { "/api/v1/export/inventory", "", "实时库存", "条码" };
        yield return new object[] { "/api/v1/export/stock-logs", "", "库存流水", "时间" };
        yield return new object[] { "/api/v1/export/credits", "", "赊账记录", "微信号" };
        yield return new object[] { "/api/v1/export/stock-warnings", "", "采购建议", "条码" };
        yield return new object[] { "/api/v1/export/daily-sales", "?date=" + TodayText, "日销售Top5", "商品" };
        yield return new object[] { "/api/v1/export/supplier-statement", "?supplierId=1", "供应商对账单", "类型" };
    }

    /// <summary>拼接导出请求（format 用 & 追加，避免和已有查询串里的 ? 打架）</summary>
    private Task<HttpResponseMessage> ExportAsync(string path, string query, string? format = null) =>
        Client.GetAsync(path + query + (format == null ? "" : (query.Length == 0 ? "?" : "&") + "format=" + format));

    // ================= 1. 鉴权 =================

    [Fact]
    public async Task 未登录访问导出端点全部返回401()
    {
        foreach (var row in Endpoints())
        {
            var path = (string)row[0];
            var query = (string)row[1];
            var resp = await Client.GetAsync(path + query);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    // ================= 2. 默认 xlsx：能打开、表头对、中文文件名有编码 =================

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task 导出端点_默认返回可打开的xlsx且表头正确(string path, string query, string sheetName, string firstHeader)
    {
        await LoginAsAdminAsync();
        var resp = await ExportAsync(path, query);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(XlsxMime, resp.Content.Headers.ContentType?.MediaType);

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        // xlsx 是 zip：前两字节必须是 PK，否则就是一个被当成 xlsx 返回的假文件
        Assert.True(bytes.Length > 4, $"{path} 导出内容不应为空");
        Assert.Equal((byte)0x50, bytes[0]);
        Assert.Equal((byte)0x4B, bytes[1]);

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);
        Assert.Equal(sheetName, ws.Name);
        Assert.Equal(firstHeader, ws.Cell(2, 1).GetString());   // 第 1 行是标题行、第 2 行才是表头

        // 中文文件名必须由 filename*（RFC 5987）承载，否则浏览器落盘是乱码文件名
        var disposition = resp.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.False(string.IsNullOrWhiteSpace(disposition!.FileNameStar),
            $"{path} 的中文文件名应写在 filename* 里");
    }

    // ================= 3. format=csv：同一份数据换一种格式 =================

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task 导出端点_CSV格式返回带BOM的文本且表头正确(string path, string query, string sheetName, string firstHeader)
    {
        await LoginAsAdminAsync();
        var resp = await ExportAsync(path, query, "csv");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);

        var text = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith("\ufeff", text);                  // BOM：Excel 双击打开不乱码
        Assert.Contains("\"" + firstHeader + "\"", text);   // 表头是中文列标题

        var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
                       ?? resp.Content.Headers.ContentDisposition?.FileName;
        Assert.NotNull(fileName);
        Assert.Contains(".csv", fileName);

        _ = sheetName;   // CSV 没有工作表概念，两个用例共用同一张数据表驱动
    }

    // ================= 4. 反射取数的三条具体防线（本文件最有价值的三条）=================

    [Fact]
    public async Task 供应商对账单导出_进货与退货行都在且合计为净应付()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITEXPSS"), salePrice: 9m, costPrice: 5m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));

        var purchaseOrderNo = (await SeedPurchaseAsync(supplierId, (productId, 20m, 5m)))
            .GetProperty("orderNo").GetString()!;                                       // 100
        var returnResp = await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 3m, costPrice = 5m } },
            returnTotal = 15m,
        });
        var returnOrderNo = (await ExpectOk(returnResp)).GetProperty("orderNo").GetString()!;

        var csv = await (await ExportAsync("/api/v1/export/supplier-statement", $"?supplierId={supplierId}", "csv"))
            .Content.ReadAsStringAsync();

        // 这两条就是「GetProperty("items") 返回 null ⇒ 导出静默变空表」的解药
        Assert.Contains(purchaseOrderNo, csv);
        Assert.Contains(returnOrderNo, csv);
        Assert.Contains("-15.00", csv);      // 退货记负
        Assert.Contains("净应付", csv);       // 合计行标签
        Assert.Contains("85.00", csv);       // 净应付 = 100 − 15
    }

    [Fact]
    public async Task 日销售Top5导出_含当日成交商品()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITEXPD");
        var productId = await SeedProductAsync(name, salePrice: 7m, costPrice: 4m, stock: 5m);
        await SeedSaleAsync((productId, 2m, 7m));

        var csv = await (await ExportAsync("/api/v1/export/daily-sales", $"?date={TodayText}", "csv"))
            .Content.ReadAsStringAsync();

        // 守的是 GetProperty("top5")：取不到就是空表
        Assert.Contains(name, csv);
        Assert.Contains("14.00", csv);       // 2 × 7
    }

    [Fact]
    public async Task 库存预警导出_带建议补货列且缺货商品在表里()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITEXPW");
        // 库存 0、安全库存 4 ⇒ 必然进预警表；建议补货 = max(4*2−0, 4) = 8
        await SeedProductAsync(name, salePrice: 5m, costPrice: 3m, stock: 0m, safetyStock: 4m);

        var bytes = await (await ExportAsync("/api/v1/export/stock-warnings", "")).Content.ReadAsByteArrayAsync();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 2;
        var nameCol = 0;
        var suggestedCol = 0;
        var statusCol = 0;
        for (var c = 1; c <= 10; c++)
        {
            var title = ws.Cell(2, c).GetString();
            if (title == "名称") nameCol = c;
            if (title == "建议补货") suggestedCol = c;
            if (title == "状态") statusCol = c;
        }
        Assert.True(nameCol > 0 && suggestedCol > 0 && statusCol > 0, "导出表头应含 名称/建议补货/状态 三列");

        var row = Enumerable.Range(3, Math.Max(0, lastRow - 2))
            .FirstOrDefault(r => ws.Cell(r, nameCol).GetString() == name);
        Assert.True(row > 0, "缺货商品应出现在预警导出里（守的是 Props() 反射取匿名对象）");
        Assert.Equal(8d, ws.Cell(row, suggestedCol).GetDouble());
        Assert.Equal("缺货", ws.Cell(row, statusCol).GetString());
    }

    [Fact]
    public async Task 导出采购进货单_合计行带标签且金额列等于数据行求和()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITEXPS"), salePrice: 9m, costPrice: 4m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 3m, 4m));      // 12 元

        var csv = await (await ExportAsync("/api/v1/export/purchases", "", "csv")).Content.ReadAsStringAsync();
        var cells = csv.TrimStart('\ufeff').TrimEnd('\r', '\n').Split('\n').Select(l => l.Split(',')).ToList();

        var amountCol = Array.IndexOf(cells[0], "\"总金额\"");
        Assert.True(amountCol > 0, "导出表头应含「总金额」列");

        // 合计行 = 最后一行：标签列（第一列）必须显示「合计」。
        // 这一条守的是 ExcelExportService.LabelColumnIndex 的「默认第一列」回退 ——
        // 各导出端点的列定义里**没人显式设过 IsLabel**，缺了回退，末行就是一行只有数字、没有标签的哑行。
        var summary = cells[^1];
        Assert.Equal("\"合计\"", summary[0]);

        // 恒等式：金额列的合计 = 所有数据行该列之和（本类共享一个库，所以不硬编码 12）
        var dataRows = cells.Skip(1).Take(cells.Count - 2).ToList();
        Assert.NotEmpty(dataRows);
        var dataSum = dataRows.Sum(r => decimal.Parse(r[amountCol].Trim('"')));
        Assert.True(dataSum >= 12m, $"合计应包含本用例的 12 元，实际 {dataSum}");
        Assert.Equal(dataSum, decimal.Parse(summary[amountCol].Trim('"')));
    }

    // ================= 5. 筛选参数真的传到 Service 了 =================

    [Fact]
    public async Task 导出商品_关键词筛选只导出命中的商品()
    {
        await LoginAsAdminAsync();
        var keep = NewTag("ITEXPK");
        var drop = NewTag("ITEXPN");
        await SeedProductAsync(keep, salePrice: 3m, costPrice: 1m);
        await SeedProductAsync(drop, salePrice: 3m, costPrice: 1m);

        var csv = await (await ExportAsync("/api/v1/export/products", $"?keyword={keep}", "csv"))
            .Content.ReadAsStringAsync();

        Assert.Contains(keep, csv);
        Assert.DoesNotContain(drop, csv);
    }

    [Fact]
    public async Task 导出库存流水_按变动类型筛选()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITEXPL");
        var productId = await SeedProductAsync(name, salePrice: 5m, costPrice: 2m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 6m, 2m));      // 写一条「采购入库」流水

        var csv = await (await ExportAsync($"/api/v1/export/stock-logs", $"?keyword={name}&changeType=采购入库", "csv"))
            .Content.ReadAsStringAsync();

        Assert.Contains(name, csv);
        Assert.Contains("采购入库", csv);
        Assert.DoesNotContain("销售出库", csv);
    }

    [Fact]
    public async Task 导出对账单_缺参数时给空表而不是500()
    {
        await LoginAsAdminAsync();
        // supplierId 缺失（int 默认 0）⇒ 应返回一张空表，而不是 500
        var resp = await ExportAsync("/api/v1/export/supplier-statement", "", "csv");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var lines = (await resp.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim('\r', '\ufeff')).ToList();

        // 只有两行：表头 + 合计行（合计行在无数据时也照常输出，值为 0）
        Assert.Equal(2, lines.Count);
        Assert.Contains("类型", lines[0]);
        Assert.Contains("净应付", lines[1]);
        Assert.Contains("0.00", lines[1]);
    }
}
