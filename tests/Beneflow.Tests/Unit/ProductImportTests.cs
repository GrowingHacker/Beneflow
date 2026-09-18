using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 商品档案 Excel 导入测试：表头同义词识别、列映射覆盖、逐行校验、条码占用判定，
/// 以及落库时的「新增 / 跳过 / 覆盖更新」三种行为与期初建账流水。
/// </summary>
public class ProductImportTests : TestBase
{
    // ================= 造一个真实的 .xlsx（导入走 ClosedXML，不能只 mock 字节）=================

    private static IFormFile NewXlsx(string[] headers, params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("商品档案");
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = rows[r][c];

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "商品导入.xlsx");
    }

    private static List<object> RowsOf(object? data) => Prop<List<object>>(data, "rows") ?? new List<object>();
    private static Dictionary<string, string?> MappingOf(object? data) =>
        Prop<Dictionary<string, string?>>(data, "mapping") ?? new Dictionary<string, string?>();
    private static object? RowAt(object? data, int index) => RowsOf(data).ElementAtOrDefault(index);
    private static List<string> MessagesOf(object? row) => Prop<List<string>>(row, "messages") ?? new List<string>();

    // ================= 解析：表头识别 =================

    [Fact]
    public async Task ParseImport_AutoMatchSynonymHeaders()
    {
        // 列名全部是「别人家的叫法」，考验同义词识别
        var file = NewXlsx(
            new[] { "条形码", "品名", "类别", "单位", "零售价", "进价", "库存数量" },
            new[] { "6901111111111", "农夫山泉", "饮料", "瓶", "2.00", "1.20", "48" });

        var r = await ProductSvc.ParseImportAsync(file, null);

        Assert.Equal(0, r.Code);
        var mapping = MappingOf(r.Data);
        Assert.Equal("条形码", mapping["barcode"]);
        Assert.Equal("品名", mapping["name"]);
        Assert.Equal("类别", mapping["category"]);
        Assert.Equal("零售价", mapping["salePrice"]);
        Assert.Equal("进价", mapping["costPrice"]);
        Assert.Equal("库存数量", mapping["stock"]);

        var row = RowAt(r.Data, 0);
        Assert.False(MessagesOf(row).Any());
        Assert.Equal(2.00m, Prop<decimal>(row, "salePrice"));
        Assert.Equal(48m, Prop<decimal>(row, "stockQuantity"));
        Assert.False(Prop<bool>(row, "isExisting"));
    }

    [Fact]
    public async Task ParseImport_MissingNameColumn_AsksForMappingInsteadOfFailing()
    {
        // 认不出「商品名称」时不应该直接报错，而是把表头带回去让用户在预览页手动指定列映射
        var file = NewXlsx(new[] { "货品", "零售价" }, new[] { "牛奶", "3.50" });

        var r = await ProductSvc.ParseImportAsync(file, null);

        Assert.Equal(0, r.Code);
        Assert.True(Prop<bool>(r.Data, "needMapping"));
        Assert.Empty(RowsOf(r.Data));
        Assert.Contains("货品", Prop<List<string>>(r.Data, "headers")!);
    }

    [Fact]
    public async Task ParseImport_ExplicitMapping_OverridesAutoDetection()
    {
        var file = NewXlsx(new[] { "货品", "零售价" }, new[] { "牛奶", "3.50" });

        var r = await ProductSvc.ParseImportAsync(file, """{"name":"货品","salePrice":"零售价"}""");

        Assert.Equal(0, r.Code);
        Assert.False(Prop<bool>(r.Data, "needMapping"));
        var row = RowAt(r.Data, 0);
        Assert.Equal("牛奶", Prop<string>(row, "name"));
        Assert.Equal(3.50m, Prop<decimal>(row, "salePrice"));
    }

    [Fact]
    public async Task ParseImport_EmptyMappingValue_KeepsColumnUnmapped()
    {
        // 用户把「库存」清空 ⇒ 该列不再自动补回，库存按 0 处理（而不是识别成 50）
        var file = NewXlsx(
            new[] { "商品名称", "库存" },
            new[] { "牛奶", "50" });

        var r = await ProductSvc.ParseImportAsync(file, """{"name":"商品名称","stock":""}""");

        var row = RowAt(r.Data, 0);
        Assert.Equal(0m, Prop<decimal>(row, "stockQuantity"));
    }

    // ================= 解析：逐行校验 =================

    [Fact]
    public async Task ParseImport_InvalidRows_CollectedAsMessages()
    {
        var file = NewXlsx(
            new[] { "商品名称", "售价", "成本价" },
            new[] { "", "1.00", "0.50" },        // 名称为空
            new[] { "负数售价", "-1", "0.50" },   // 售价为负
            new[] { "正常商品", "3.00", "1.00" });

        var r = await ProductSvc.ParseImportAsync(file, null);

        var rows = RowsOf(r.Data);
        Assert.Equal(3, rows.Count);
        Assert.Contains("商品名称为空", MessagesOf(rows[0]));
        Assert.Contains("售价不能为负", MessagesOf(rows[1]));
        Assert.False(MessagesOf(rows[2]).Any());

        var summary = Prop<object>(r.Data, "summary");
        Assert.Equal(3, Prop<int>(summary, "totalRows"));
        Assert.Equal(1, Prop<int>(summary, "newCount"));
        Assert.Equal(2, Prop<int>(summary, "errorCount"));
    }

    [Fact]
    public async Task ParseImport_DuplicateBarcodeInFile_FlaggedOnSecondRow()
    {
        var file = NewXlsx(
            new[] { "条码", "商品名称" },
            new[] { "6900000000001", "商品一" },
            new[] { "6900000000001", "商品二" });

        var r = await ProductSvc.ParseImportAsync(file, null);

        var rows = RowsOf(r.Data);
        Assert.False(MessagesOf(rows[0]).Any());
        Assert.Contains(MessagesOf(rows[1]), m => m.Contains("条码在文件中重复"));
    }

    [Fact]
    public async Task ParseImport_ExistingBarcode_MarkedAsExisting()
    {
        // 种子数据里已有条码 A001 的商品
        var file = NewXlsx(
            new[] { "条码", "商品名称" },
            new[] { "A001", "改名后的商品A" },
            new[] { "NEW001", "全新商品" });

        var r = await ProductSvc.ParseImportAsync(file, null);

        var rows = RowsOf(r.Data);
        Assert.True(Prop<bool>(rows[0], "isExisting"));
        Assert.False(Prop<bool>(rows[1], "isExisting"));
        var summary = Prop<object>(r.Data, "summary");
        Assert.Equal(1, Prop<int>(summary, "existingCount"));
        Assert.Equal(1, Prop<int>(summary, "newCount"));
    }

    [Fact]
    public async Task ParseImport_BarcodeHeldByDeletedProduct_ReportedAsError()
    {
        // 软删除商品仍占着条码唯一索引，直接新增会撞索引报 500，所以要在预览阶段就拦下
        var p = await Db.Products.FirstAsync(x => x.Id == ProductAId);
        p.IsDeleted = true;
        await Db.SaveChangesAsync();

        var file = NewXlsx(new[] { "条码", "商品名称" }, new[] { "A001", "想复用条码的商品" });
        var r = await ProductSvc.ParseImportAsync(file, null);

        Assert.Contains(MessagesOf(RowAt(r.Data, 0)), m => m.Contains("已删除的商品占用"));
    }

    [Fact]
    public async Task ParseImport_BlankTrailingRows_Ignored()
    {
        var file = NewXlsx(
            new[] { "商品名称", "售价" },
            new[] { "只有一行数据", "1.00" },
            new[] { "", "" },
            new[] { "", "" });

        var r = await ProductSvc.ParseImportAsync(file, null);

        Assert.Single(RowsOf(r.Data));
    }

    [Fact]
    public async Task ParseImport_NonExcelFile_Rejected()
    {
        var bytes = "not an excel"u8.ToArray();
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "商品.csv");

        var r = await ProductSvc.ParseImportAsync(file, null);

        Assert.NotEqual(0, r.Code);
        Assert.Contains("仅支持", r.Message);
    }

    [Fact]
    public async Task ParseImport_NoCategoryColumn_NewCategoryListIsEmpty()
    {
        var file = NewXlsx(new[] { "商品名称" }, new[] { "没填分类的商品" });

        var r = await ProductSvc.ParseImportAsync(file, null);
        var summary = Prop<object>(r.Data, "summary");

        // 没填分类走「未分类」，且该分类尚未建过 ⇒ 预览里会提示将自动创建
        Assert.Contains("未分类", Prop<List<string>>(summary, "newCategories")!);
    }

    [Fact]
    public async Task ParseImport_ExistingCategory_NotListedAsNew()
    {
        var file = NewXlsx(new[] { "商品名称", "分类" }, new[] { "商品", "测试分类" });

        var r = await ProductSvc.ParseImportAsync(file, null);
        var summary = Prop<object>(r.Data, "summary");

        Assert.Empty(Prop<List<string>>(summary, "newCategories")!);
    }

    // ================= 落库：新增 =================

    [Fact]
    public async Task ImportBatch_CreatesProducts_WritesInitialStockAndBatch()
    {
        var dto = new ProductImportDto
        {
            Rows =
            {
                new ProductImportRowDto
                {
                    Row = 2, Barcode = "IMP001", Name = "花生米", CategoryName = "坚果",
                    Unit = "袋", Spec = "500g", SalePrice = 12.5m, CostPrice = 8m,
                    StockQuantity = 30m, SafetyStock = 5m, ShelfLifeDays = 180,
                },
                new ProductImportRowDto
                {
                    Row = 3, Barcode = "IMP002", Name = "一次性纸杯", CategoryName = "日杂",
                    Unit = "包", SalePrice = 6m, CostPrice = 3m, StockQuantity = 0m,
                },
            },
        };

        var r = await ProductSvc.ImportBatchAsync(dto);

        Assert.Equal(0, r.Code);
        Assert.Equal(2, Prop<int>(r.Data, "created"));
        Assert.Equal(0, Prop<int>(r.Data, "updated"));

        var p = await Db.Products.AsNoTracking().FirstAsync(x => x.Barcode == "IMP001");
        Assert.Equal("花生米", p.Name);
        Assert.Equal("袋", p.Unit);
        Assert.Equal(30m, p.StockQuantity);
        Assert.True(p.HasExpiry);
        Assert.Equal(180, p.ShelfLifeDays);
        Assert.Equal("HSM", p.PinyinCode);                       // 拼音码供收银台首字母检索
        Assert.True(p.Status);

        // 分类不存在时自动创建
        var cat = await Db.Categories.AsNoTracking().FirstAsync(c => c.Name == "坚果");
        Assert.Equal(cat.Id, p.CategoryId);

        // 期初库存要留下「期初建账」流水，库存变动必须有凭据
        var log = await Db.StockLogs.AsNoTracking().FirstAsync(s => s.ProductId == p.Id);
        Assert.Equal("期初建账", log.ChangeType);
        Assert.Equal(30m, log.ChangeQty);

        // 有效期商品要建批次
        Assert.True(await Db.Batches.AsNoTracking().AnyAsync(b => b.ProductId == p.Id && b.Quantity == 30m));

        // 无库存商品不写流水
        var p2 = await Db.Products.AsNoTracking().FirstAsync(x => x.Barcode == "IMP002");
        Assert.False(await Db.StockLogs.AsNoTracking().AnyAsync(s => s.ProductId == p2.Id));

        Assert.True(await Db.OperationLogs.AsNoTracking().AnyAsync(l => l.Module == "商品管理" && l.Action == "批量导入商品"));
    }

    [Fact]
    public async Task ImportBatch_EmptyBarcode_GeneratesInStoreCode()
    {
        var dto = new ProductImportDto
        {
            Rows = { new ProductImportRowDto { Row = 2, Name = "散装瓜子", Unit = "斤", IsWeighted = true, StockQuantity = 5m } },
        };

        var r = await ProductSvc.ImportBatchAsync(dto);

        Assert.Equal(0, r.Code);
        var p = await Db.Products.AsNoTracking().FirstAsync(x => x.Name == "散装瓜子");
        Assert.StartsWith("L", p.Barcode);
        Assert.Equal("斤", p.Unit);
        Assert.True(p.IsWeighted);
    }

    [Fact]
    public async Task ImportBatch_DefaultUnitByWeightedFlag()
    {
        var dto = new ProductImportDto
        {
            Rows =
            {
                new ProductImportRowDto { Row = 2, Name = "有单位", Unit = "箱" },
                new ProductImportRowDto { Row = 3, Name = "无单位非称重" },
                new ProductImportRowDto { Row = 4, Name = "无单位称重", IsWeighted = true },
            },
        };

        await ProductSvc.ImportBatchAsync(dto);

        Assert.Equal("箱", (await Db.Products.AsNoTracking().FirstAsync(x => x.Name == "有单位")).Unit);
        Assert.Equal("件", (await Db.Products.AsNoTracking().FirstAsync(x => x.Name == "无单位非称重")).Unit);
        Assert.Equal("斤", (await Db.Products.AsNoTracking().FirstAsync(x => x.Name == "无单位称重")).Unit);
    }

    // ================= 落库：条码已存在 =================

    [Fact]
    public async Task ImportBatch_ExistingBarcode_WithSkipPolicy_LeavesProductUntouched()
    {
        SetStock(ProductAId, 7m, 4.5m);
        var dto = new ProductImportDto
        {
            DuplicatePolicy = "skip",
            Rows = { new ProductImportRowDto { Row = 2, Barcode = "A001", Name = "想改的名字", SalePrice = 99m, StockQuantity = 500m } },
        };

        var r = await ProductSvc.ImportBatchAsync(dto);

        Assert.Equal(0, r.Code);
        Assert.Equal(0, Prop<int>(r.Data, "created"));
        Assert.Equal(1, Prop<int>(r.Data, "skipped"));

        var p = GetProduct(ProductAId);
        Assert.Equal("商品A（无有效期）", p.Name);
        Assert.Equal(10.00m, p.SalePrice);
        Assert.Equal(7m, p.StockQuantity);
    }

    [Fact]
    public async Task ImportBatch_ExistingBarcode_WithUpdatePolicy_UpdatesFieldsButNotStock()
    {
        SetStock(ProductAId, 7m, 4.5m);
        var dto = new ProductImportDto
        {
            DuplicatePolicy = "update",
            Rows = { new ProductImportRowDto
            {
                Row = 2, Barcode = "A001", Name = "改名后的商品A", CategoryName = "新分类",
                Unit = "听", Spec = "330ml", SalePrice = 12m, CostPrice = 6m,
                StockQuantity = 500m, SafetyStock = 3m, ShelfLifeDays = 90, Status = false,
            } },
        };

        var r = await ProductSvc.ImportBatchAsync(dto);

        Assert.Equal(0, r.Code);
        Assert.Equal(1, Prop<int>(r.Data, "updated"));

        var p = GetProduct(ProductAId);
        Assert.Equal("改名后的商品A", p.Name);
        Assert.Equal("听", p.Unit);
        Assert.Equal("330ml", p.Spec);
        Assert.Equal(12m, p.SalePrice);
        Assert.Equal(6m, p.CostPrice);
        Assert.Equal(3m, p.SafetyStock);
        Assert.Equal(90, p.ShelfLifeDays);
        Assert.True(p.HasExpiry);
        Assert.False(p.Status);

        // 覆盖更新不动库存：库存变动必须走进货/盘点，留下流水
        Assert.Equal(7m, p.StockQuantity);
        Assert.False(await Db.StockLogs.AsNoTracking().AnyAsync(s => s.ProductId == p.Id));
    }

    [Fact]
    public async Task ImportBatch_NameMissing_ReportedAsFailed()
    {
        var dto = new ProductImportDto
        {
            Rows = { new ProductImportRowDto { Row = 5, Name = "  ", Barcode = "X1" } },
        };

        var r = await ProductSvc.ImportBatchAsync(dto);

        Assert.Equal(0, r.Code);
        var failed = Prop<List<object>>(r.Data, "failed")!;
        Assert.Single(failed);
        Assert.Equal(5, Prop<int>(failed[0], "row"));
        Assert.False(await Db.Products.AsNoTracking().AnyAsync(x => x.Barcode == "X1"));
    }

    [Fact]
    public async Task ImportBatch_EmptyRows_Rejected()
    {
        var r = await ProductSvc.ImportBatchAsync(new ProductImportDto());

        Assert.NotEqual(0, r.Code);
        Assert.Contains("没有可导入", r.Message);
    }

    [Fact]
    public async Task ImportBatch_SameNewCategoryOnManyRows_CreatedOnce()
    {
        var rows = Enumerable.Range(0, 5)
            .Select(i => new ProductImportRowDto { Row = i + 2, Barcode = $"C{i}", Name = $"商品{i}", CategoryName = "批量分类" })
            .ToList();

        await ProductSvc.ImportBatchAsync(new ProductImportDto { Rows = rows });

        Assert.Equal(1, await Db.Categories.AsNoTracking().CountAsync(c => c.Name == "批量分类"));
    }

    // ================= 模板 =================

    [Fact]
    public void BuildImportTemplate_HasGuideAndDataSheet()
    {
        var bytes = ProductSvc.BuildImportTemplate();
        Assert.True(bytes.Length > 0);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Contains("使用说明", wb.Worksheets.Select(w => w.Name));

        var ws = wb.Worksheet("商品档案");
        Assert.Equal("条码", ws.Cell(1, 1).GetString());
        Assert.Equal("商品名称", ws.Cell(1, 2).GetString());
        Assert.Equal("分类", ws.Cell(1, 3).GetString());
        Assert.Equal("成本价", ws.Cell(1, 7).GetString());
        Assert.Equal("是否称重", ws.Cell(1, 11).GetString());
    }

    [Fact]
    public async Task BuildImportTemplate_IsParsableByImportParser()
    {
        // 模板表头与导入解析器必须对得上：否则用户下载模板填完反而导不进去
        var bytes = ProductSvc.BuildImportTemplate();
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("商品档案");
        ws.Rows(2, ws.LastRowUsed()!.RowNumber()).Delete();   // 删掉示例行
        var filled = new[] { "TM001", "模板商品", "测试分类", "瓶", "550ml", "3.50", "2.00", "12", "3", "", "否", "上架", "" };
        for (var i = 0; i < filled.Length; i++) ws.Cell(2, i + 1).Value = filled[i];

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var data = ms.ToArray();
        var file = new FormFile(new MemoryStream(data), 0, data.Length, "file", "商品导入模板.xlsx");

        var r = await ProductSvc.ParseImportAsync(file, null);

        Assert.Equal(0, r.Code);
        Assert.False(Prop<bool>(r.Data, "needMapping"));
        var row = RowAt(r.Data, 0);
        Assert.Equal("模板商品", Prop<string>(row, "name"));
        Assert.Equal("测试分类", Prop<string>(row, "categoryName"));
        Assert.Equal(3.50m, Prop<decimal>(row, "salePrice"));
        Assert.Equal(2.00m, Prop<decimal>(row, "costPrice"));
        Assert.Equal(12m, Prop<decimal>(row, "stockQuantity"));
        Assert.Equal(3m, Prop<decimal>(row, "safetyStock"));
        Assert.False(MessagesOf(row).Any());
    }
}
