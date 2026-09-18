using System.Text;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 进货单 Excel 导入解析测试。
///
/// 覆盖三件事：
/// 1. Sheet 名 → 供应商的匹配规则（含「使用说明」跳过、不匹配进 unmatchedSheets）；
/// 2. 行级分流：匹配到商品 / 未匹配商品（unmatchedProducts）/ 非法数量进价（errors）/ 整表跳过（skippedSheets）；
/// 3. 「预览 → 批量建单」往返：解析出来的一堆匿名对象，按前端 confirmImport 的映射方式
///    转成 DTO 后，是否真的能落成进货单并改动库存与成本。
///
/// 背景：进货导入此前只有 4 条 HTTP 契约测试，解析层零覆盖，这里是补上的一层。
/// </summary>
public class PurchaseImportTests : TestBase
{
    // ================= 造真实 .xlsx（导入走 ClosedXML，不能只 mock 字节）=================

    private sealed record Sheet(string Name, string[] Headers, string[][] Rows);

    /// <summary>与导入模板一致的标准表头</summary>
    private static readonly string[] Cols = { "条码", "商品名称", "数量", "进价" };
    private static readonly string[] ColsWithDate = { "条码", "商品名称", "数量", "进价", "生产日期" };

    private static IFormFile NewXlsx(params Sheet[] sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var s in sheets)
        {
            var ws = wb.Worksheets.Add(s.Name);
            for (var c = 0; c < s.Headers.Length; c++) ws.Cell(1, c + 1).Value = s.Headers[c];
            for (var r = 0; r < s.Rows.Length; r++)
                for (var c = 0; c < s.Rows[r].Length; c++)
                    ws.Cell(r + 2, c + 1).Value = s.Rows[r][c];
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "进货单导入.xlsx");
    }

    private static IFormFile Single(string sheetName, string[] headers, params string[][] rows) =>
        NewXlsx(new Sheet(sheetName, headers, rows));

    // ---- 读取解析结果里的匿名对象 ----
    private static List<object> Orders(object? data) => Prop<List<object>>(data, "orders") ?? new();
    private static List<object> ItemsOf(object order) => Prop<List<object>>(order, "items") ?? new();
    private static List<object> ErrorsOf(object order) => Prop<List<object>>(order, "errors") ?? new();
    private static List<object> UnmatchedProducts(object? data) =>
        Prop<List<object>>(data, "unmatchedProducts") ?? new();
    private static List<string> StringsOf(object? data, string key) =>
        Prop<List<string>>(data, key) ?? new();

    // ================= Sheet 名 → 供应商 =================

    [Fact]
    public async Task ParseImport_MatchingSheet_ReturnsMatchedItemsAndRoundedTotals()
    {
        // Sheet 名 = 供应商名「测试供应商」；两行都按条码命中种子商品 A001 / B001
        var file = Single("测试供应商", ColsWithDate,
            new[] { "A001", "商品A（无有效期）", "3", "5.00", "" },
            new[] { "B001", "商品B（有有效期）", "2", "8.00", "2026-01-01" });

        var r = await PurchaseSvc.ParseImportAsync(file);

        Assert.Equal(0, r.Code);
        var order = Assert.Single(Orders(r.Data));
        Assert.Equal(SupplierId, Prop<int>(order, "supplierId"));
        Assert.Equal("测试供应商", Prop<string>(order, "supplierName"));
        Assert.Equal(2, Prop<int>(order, "rowCount"));
        Assert.Equal(5m, Prop<decimal>(order, "totalQty"));      // 3 + 2
        Assert.Equal(31m, Prop<decimal>(order, "totalAmount"));  // 3×5 + 2×8

        var items = ItemsOf(order);
        Assert.Equal(2, items.Count);

        Assert.Equal(ProductAId, Prop<int>(items[0], "productId"));
        Assert.Equal("A001", Prop<string>(items[0], "barcode"));
        Assert.Equal("瓶", Prop<string>(items[0], "unit"));
        Assert.False(Prop<bool>(items[0], "isWeighted"));
        Assert.Equal(3m, Prop<decimal>(items[0], "qty"));
        Assert.Equal(5.00m, Prop<decimal>(items[0], "costPrice"));
        Assert.Null(Prop<string>(items[0], "produceDate"));   // 空单元格 → null 而不是空串

        Assert.Equal(ProductBId, Prop<int>(items[1], "productId"));
        Assert.Equal("2026-01-01", Prop<string>(items[1], "produceDate"));

        Assert.Empty(ErrorsOf(order));
        Assert.Empty(StringsOf(r.Data, "unmatchedSheets"));
        Assert.Empty(UnmatchedProducts(r.Data));
        Assert.Equal(2, Prop<int>(r.Data, "totalRows"));
    }

    [Fact]
    public async Task ParseImport_SheetNameNotMatchingAnySupplier_ReportedAsUnmatchedSheet()
    {
        var file = Single("根本没有这个供应商", Cols, new[] { "A001", "商品A（无有效期）", "1", "5.00" });

        // 未匹配不算失败：前端要拿这个名单去引导用户新建供应商
        var r = await PurchaseSvc.ParseImportAsync(file);

        Assert.Equal(0, r.Code);
        Assert.Empty(Orders(r.Data));
        Assert.Equal(new[] { "根本没有这个供应商" }, StringsOf(r.Data, "unmatchedSheets"));
    }

    [Fact]
    public async Task ParseImport_GuideSheet_IgnoredAndNotReportedAsUnmatched()
    {
        // 模板自带的「使用说明」页必须静默跳过，既不当订单也不当未匹配供应商
        var file = NewXlsx(
            new Sheet("使用说明", new[] { "进货单批量导入说明" }, new[] { new[] { "1. 每个 Sheet 对应一张进货单" } }),
            new Sheet("测试供应商", Cols, new[] { new[] { "A001", "商品A（无有效期）", "1", "5.00" } }));

        var r = await PurchaseSvc.ParseImportAsync(file);

        Assert.Equal(0, r.Code);
        Assert.Single(Orders(r.Data));
        Assert.Empty(StringsOf(r.Data, "unmatchedSheets"));
        Assert.Empty(StringsOf(r.Data, "skippedSheets"));
        Assert.Equal(2, Prop<int>(r.Data, "totalSheets"));
        Assert.Equal(1, Prop<int>(r.Data, "totalRows"));
    }

    // ================= 行级分流 =================

    [Fact]
    public async Task ParseImport_UnknownProduct_ReportedAsUnmatchedProductNotAsError()
    {
        // 条码与名称都查不到 → 进 unmatchedProducts（前端弹窗建商品），但不阻断同 Sheet 里能匹配的行
        var file = Single("测试供应商", Cols,
            new[] { "X999", "查无此货", "2", "3.00" },
            new[] { "A001", "商品A（无有效期）", "4", "5.00" });

        var r = await PurchaseSvc.ParseImportAsync(file);

        Assert.Equal(0, r.Code);
        var order = Assert.Single(Orders(r.Data));
        Assert.Single(ItemsOf(order));                        // 只有能匹配的那行进了明细
        Assert.Equal(4m, Prop<decimal>(order, "totalQty"));

        var unmatched = Assert.Single(UnmatchedProducts(r.Data));
        Assert.Equal("测试供应商", Prop<string>(unmatched, "sheetName"));
        Assert.Equal(2, Prop<int>(unmatched, "row"));         // Excel 里的实际行号（含表头行）
        Assert.Equal("X999", Prop<string>(unmatched, "barcode"));
        Assert.Equal("查无此货", Prop<string>(unmatched, "name"));
    }

    [Fact]
    public async Task ParseImport_InvalidQtyOrPrice_CollectedAsRowErrors()
    {
        var file = Single("测试供应商", Cols,
            new[] { "A001", "商品A（无有效期）", "0", "5.00" },    // 数量 0
            new[] { "A001", "商品A（无有效期）", "2", "-1" },      // 进价为负
            new[] { "A001", "商品A（无有效期）", "2", "5.00" });   // 正常

        var r = await PurchaseSvc.ParseImportAsync(file);

        var order = Assert.Single(Orders(r.Data));
        var errors = ErrorsOf(order);
        Assert.Equal(2, errors.Count);
        Assert.Contains("数量必须大于 0", Prop<string>(errors[0], "message"));
        Assert.Contains("进价不能为负", Prop<string>(errors[1], "message"));

        // 只有合法的第 3 行进入明细，合计也只算它
        Assert.Single(ItemsOf(order));
        Assert.Equal(2m, Prop<decimal>(order, "totalQty"));
        Assert.Equal(10m, Prop<decimal>(order, "totalAmount"));
    }

    [Fact]
    public async Task ParseImport_ProblemSheets_AreSkippedWithoutBreakingOtherSheets()
    {
        // 两个「有问题的 Sheet」：一个缺数量/进价列，一个有表头但无数据行 —— 都进 skippedSheets，
        // 但不影响同一个文件里正常的 Sheet 照常导入。
        Db.Suppliers.AddRange(new Supplier { Name = "缺列供应商" }, new Supplier { Name = "无行供应商" });
        await Db.SaveChangesAsync();

        var file = NewXlsx(
            new Sheet("缺列供应商", new[] { "条码", "商品名称" }, new[] { new[] { "A001", "商品A（无有效期）" } }),
            new Sheet("无行供应商", Cols, Array.Empty<string[]>()),
            new Sheet("测试供应商", Cols, new[] { new[] { "A001", "商品A（无有效期）", "1", "5.00" } }));

        var r = await PurchaseSvc.ParseImportAsync(file);

        Assert.Equal(0, r.Code);
        Assert.Single(Orders(r.Data));                        // 正常的 Sheet 照样导入
        var skipped = StringsOf(r.Data, "skippedSheets");
        Assert.Equal(2, skipped.Count);
        Assert.Contains(skipped, s => s.StartsWith("缺列供应商") && s.Contains("缺少数量/进价列"));
        Assert.Contains(skipped, s => s.StartsWith("无行供应商") && s.Contains("无数据行"));
    }

    [Fact]
    public async Task ParseImport_OnlySkippedSheets_ReturnsChineseFail()
    {
        // 唯一 Sheet 缺数量/进价列 ⇒ 既没订单、也没未匹配供应商/商品 ⇒ 应明确失败，而不是给个空预览让前端弹空窗
        var file = Single("测试供应商", new[] { "条码", "商品名称" }, new[] { "A001", "商品A（无有效期）" });

        var r = await PurchaseSvc.ParseImportAsync(file);

        Assert.NotEqual(0, r.Code);
        Assert.Contains("未解析到有效进货单", r.Message);
    }

    // ================= 商品匹配策略 =================

    [Fact]
    public async Task ParseImport_BarcodeMatchWinsOverMismatchedName()
    {
        // 条码 A001 指向商品A，名称却写错了 —— 仍应按条码命中，不去拿错误名称建新商品
        var file = Single("测试供应商", Cols, new[] { "A001", "名字写错了", "2", "5.00" });

        var r = await PurchaseSvc.ParseImportAsync(file);

        var order = Assert.Single(Orders(r.Data));
        var item = Assert.Single(ItemsOf(order));
        Assert.Equal(ProductAId, Prop<int>(item, "productId"));
        Assert.Empty(UnmatchedProducts(r.Data));
    }

    [Fact]
    public async Task ParseImport_WeightedProduct_MatchedByNameWhenBarcodeEmpty()
    {
        // 散装称重商品没有条码，只能靠名称匹配（这也是模板里「条码可留空」的用意）
        Db.Products.Add(new Product
        {
            Name = "散装花生米", Barcode = "", CategoryId = CategoryId, Unit = "斤",
            SalePrice = 12.00m, CostPrice = 8.00m, StockQuantity = 0,
            IsWeighted = true, IsDeleted = false, CreatedAt = DateTime.Now,
        });
        await Db.SaveChangesAsync();

        var file = Single("测试供应商", Cols, new[] { "", "散装花生米", "2.5", "8.00" });

        var r = await PurchaseSvc.ParseImportAsync(file);

        var order = Assert.Single(Orders(r.Data));
        var item = Assert.Single(ItemsOf(order));
        Assert.Equal("散装花生米", Prop<string>(item, "name"));
        Assert.Equal("斤", Prop<string>(item, "unit"));
        Assert.True(Prop<bool>(item, "isWeighted"));
        Assert.Equal(2.5m, Prop<decimal>(item, "qty"));
    }

    [Fact]
    public async Task ParseImport_SynonymHeaders_AreRecognised()
    {
        // 用户手里的表头往往不是我们模板的用词：条形码 / 品名 / 成本价
        var file = Single("测试供应商", new[] { "条形码", "品名", "数量", "成本价" },
            new[] { "B001", "商品B（有有效期）", "6", "7.50" });

        var r = await PurchaseSvc.ParseImportAsync(file);

        var order = Assert.Single(Orders(r.Data));
        var item = Assert.Single(ItemsOf(order));
        Assert.Equal(ProductBId, Prop<int>(item, "productId"));
        Assert.Equal(6m, Prop<decimal>(item, "qty"));
        Assert.Equal(7.50m, Prop<decimal>(item, "costPrice"));
        Assert.Equal(45m, Prop<decimal>(order, "totalAmount"));
    }

    // ================= 模板 / 文件级校验 =================

    [Fact]
    public async Task ImportTemplate_CanBeParsedBack_GuideIgnoredAndSampleSheetUnmatched()
    {
        // 模板本身必须能被自己的解析器读回来：说明页跳过，示例页因不是真实供应商名而进 unmatchedSheets
        var bytes = PurchaseSvc.BuildImportTemplate();
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "进货单导入模板.xlsx");

        var r = await PurchaseSvc.ParseImportAsync(file);

        Assert.Equal(0, r.Code);
        Assert.Empty(Orders(r.Data));
        Assert.Equal(new[] { "示例供应商（复制后改名）" }, StringsOf(r.Data, "unmatchedSheets"));
        Assert.Equal(2, Prop<int>(r.Data, "totalSheets"));
    }

    [Fact]
    public async Task ParseImport_EmptyOrUnsupportedFile_ReturnsChineseFail()
    {
        var empty = new FormFile(new MemoryStream(Array.Empty<byte>()), 0, 0, "file", "空文件.xlsx");
        var r1 = await PurchaseSvc.ParseImportAsync(empty);
        Assert.NotEqual(0, r1.Code);
        Assert.Contains("请选择 Excel 文件", r1.Message);

        var csvBytes = Encoding.UTF8.GetBytes("条码,商品名称,数量,进价");
        var csv = new FormFile(new MemoryStream(csvBytes), 0, csvBytes.Length, "file", "进货单.csv");
        var r2 = await PurchaseSvc.ParseImportAsync(csv);
        Assert.NotEqual(0, r2.Code);
        Assert.Contains(".xlsx", r2.Message);
    }

    // ================= 预览 → 批量建单 往返 =================

    [Fact]
    public async Task ParseImport_ThenCreateBatch_StockAndCostActuallyUpdated()
    {
        // 端到端往返：把预览结果按前端 confirmImport 的方式映射成 DTO，再走 CreateBatchAsync，
        // 验证库存/成本/批次真的被改动（解析层与落库层之间的接缝）。
        var file = Single("测试供应商", ColsWithDate,
            new[] { "A001", "商品A（无有效期）", "3", "5.00", "" },
            new[] { "B001", "商品B（有有效期）", "2", "8.00", "2026-01-01" });

        var preview = await PurchaseSvc.ParseImportAsync(file);
        var order = Assert.Single(Orders(preview.Data));

        var dto = new CreatePurchaseDto
        {
            SupplierId = Prop<int>(order, "supplierId"),
            Details = ItemsOf(order).Select(i => new PurchaseDetailDto
            {
                ProductId = Prop<int>(i, "productId"),
                Name = Prop<string>(i, "name"),
                Qty = Prop<decimal>(i, "qty"),
                CostPrice = Prop<decimal>(i, "costPrice"),
                ProduceDate = Prop<string>(i, "produceDate") is { } d ? DateTime.Parse(d) : null,
            }).ToList(),
        };

        var batch = await PurchaseSvc.CreateBatchAsync(new List<CreatePurchaseDto> { dto });

        Assert.Equal(0, batch.Code);
        Assert.Equal(1, Prop<int>(batch.Data, "total"));
        Assert.Single(Prop<List<object>>(batch.Data, "created")!);
        Assert.Empty(Prop<List<object>>(batch.Data, "failed")!);

        // 解析出来的数量/进价真的落到了库存与移动加权成本
        Assert.Equal(3m, GetProduct(ProductAId).StockQuantity);
        Assert.Equal(5.00m, GetProduct(ProductAId).CostPrice);
        Assert.Equal(2m, GetProduct(ProductBId).StockQuantity);
        Assert.Equal(8.00m, GetProduct(ProductBId).CostPrice);

        // 生产日期确实被带进批次（有效期商品）
        var batchRow = Db.Batches.AsNoTracking().Single(b => b.ProductId == ProductBId);
        Assert.Equal(new DateTime(2026, 1, 1), batchRow.ProduceDate);
        Assert.Equal(new DateTime(2026, 1, 1).AddDays(365), batchRow.ExpireDate);
    }
}
