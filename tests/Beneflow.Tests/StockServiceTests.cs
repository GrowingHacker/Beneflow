using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 库存管理单元测试：实时库存状态标签、盘点单、临期商品、库存预警、库存流水。
/// </summary>
public class StockServiceTests : TestBase
{
    /// <summary>为商品建一个批次（临期管理用）</summary>
    private ProductBatch SeedBatch(int productId, DateTime expireDate, decimal qty = 10)
    {
        var b = new ProductBatch
        {
            ProductId = productId,
            BatchNo = $"B{DateTime.Now:yyMMdd}{productId:D3}{Random.Shared.Next(1000, 9999)}",
            ProduceDate = expireDate.AddDays(-30),
            ExpireDate = expireDate,
            Quantity = qty,
        };
        Db.Batches.Add(b);
        Db.SaveChanges();
        return b;
    }

    private static CreateStockCheckDto CheckDto(params StockCheckItemDto[] items) =>
        new() { Items = items.ToList() };

    private static StockCheckItemDto CheckItem(int id, decimal bookQty, decimal actualQty) =>
        new() { Id = id, BookQty = bookQty, ActualQty = actualQty };

    // ================= 实时库存 =================

    [Fact]
    public async Task InventoryListAsync_StatusLabels()
    {
        SetStock(ProductAId, 10, 5.00m);                 // 安全库存 0 → 正常
        var pa = Db.Products.First(p => p.Id == ProductAId);
        pa.SafetyStock = 20;                             // 库存 10 ≤ 20 → 预警
        SetStock(ProductBId, 5, 8.00m);                  // 有批次 3 天后到期 → 临期
        SeedBatch(ProductBId, DateTime.Today.AddDays(3));

        var result = await StockSvc.InventoryListAsync(null, null, 1, 50);
        var list = Prop<IEnumerable<object>>(result, "list")!.ToList();
        var a = list.First(x => Prop<int>(x, "id") == ProductAId);
        var b = list.First(x => Prop<int>(x, "id") == ProductBId);

        Assert.Equal("预警", Prop<string>(a, "status"));
        Assert.Equal("临期", Prop<string>(b, "status"));
        Assert.Equal(3, Prop<int>(b, "daysLeft"));
        Assert.Equal(50.00m, Prop<decimal>(a, "stockAmount"));   // 10 × 成本 5
    }

    [Fact]
    public async Task InventoryListAsync_OutOfStock_StatusIsShortage()
    {
        var result = await StockSvc.InventoryListAsync(null, null, 1, 50);
        var list = Prop<IEnumerable<object>>(result, "list")!.ToList();
        var a = list.First(x => Prop<int>(x, "id") == ProductAId);
        Assert.Equal("缺货", Prop<string>(a, "status"));
    }

    [Fact]
    public async Task InventoryListAsync_ExpiredBatch_StatusIsExpired()
    {
        SetStock(ProductBId, 5, 8.00m);
        SeedBatch(ProductBId, DateTime.Today.AddDays(-1));

        var result = await StockSvc.InventoryListAsync(null, null, 1, 50);
        var list = Prop<IEnumerable<object>>(result, "list")!.ToList();
        var b = list.First(x => Prop<int>(x, "id") == ProductBId);
        Assert.Equal("过期", Prop<string>(b, "status"));
    }

    [Fact]
    public async Task InventoryListAsync_FilterByStatus()
    {
        SetStock(ProductAId, 10);                 // 正常
        // 商品B 库存 0 → 缺货
        var result = await StockSvc.InventoryListAsync(null, "缺货", 1, 50);
        var list = Prop<IEnumerable<object>>(result, "list")!.ToList();

        Assert.Single(list);
        Assert.Equal(ProductBId, Prop<int>(list[0], "id"));
    }

    [Fact]
    public async Task InventoryListAsync_KeywordFilter()
    {
        var result = await StockSvc.InventoryListAsync("A001", null, 1, 50);
        var list = Prop<IEnumerable<object>>(result, "list")!.ToList();
        Assert.Single(list);
        Assert.Equal(ProductAId, Prop<int>(list[0], "id"));
    }

    [Fact]
    public async Task InventoryListAsync_ExcludesDeletedProduct()
    {
        var p = Db.Products.First(x => x.Id == ProductAId);
        p.IsDeleted = true;
        await Db.SaveChangesAsync();

        var result = await StockSvc.InventoryListAsync(null, null, 1, 50);
        var list = Prop<IEnumerable<object>>(result, "list")!.ToList();
        Assert.DoesNotContain(list, x => Prop<int>(x, "id") == ProductAId);
    }

    // ================= 盘点单 =================

    [Fact]
    public async Task CreateCheckAsync_Draft_DoesNotChangeStock()
    {
        SetStock(ProductAId, 10);
        var r = await StockSvc.CreateCheckAsync(CheckDto(CheckItem(ProductAId, 10, 8)));
        Assert.Equal(0, r.Code);

        Assert.Equal(10, GetProduct(ProductAId).StockQuantity);   // 草稿不调整库存
        var check = await Db.StockChecks.AsNoTracking().FirstAsync();
        Assert.False(check.Status);
        Assert.Equal(0m, check.ProfitQty);
        Assert.Equal(2m, check.LossQty);
    }

    [Fact]
    public async Task ConfirmCheckAsync_AdjustsStockAndWritesLog()
    {
        SetStock(ProductAId, 10);
        var created = await StockSvc.CreateCheckAsync(CheckDto(CheckItem(ProductAId, 10, 8)));
        var checkId = GetResultDataProp<int>(created.Data!, "id");

        var r = await StockSvc.ConfirmCheckAsync(checkId);
        Assert.Equal(0, r.Code);

        Assert.Equal(8, GetProduct(ProductAId).StockQuantity);
        var log = LastStockLog(ProductAId)!;
        Assert.Equal("盘点调整", log.ChangeType);
        Assert.Equal(-2, log.ChangeQty);
        Assert.Equal(10, log.BeforeQty);
        Assert.Equal(8, log.AfterQty);

        var check = await Db.StockChecks.AsNoTracking().FirstAsync(c => c.Id == checkId);
        Assert.True(check.Status);
        Assert.NotNull(check.ConfirmedAt);
    }

    [Fact]
    public async Task ConfirmCheckAsync_Twice_ReturnsError()
    {
        SetStock(ProductAId, 10);
        var created = await StockSvc.CreateCheckAsync(CheckDto(CheckItem(ProductAId, 10, 8)));
        var checkId = GetResultDataProp<int>(created.Data!, "id");

        await StockSvc.ConfirmCheckAsync(checkId);
        var again = await StockSvc.ConfirmCheckAsync(checkId);

        Assert.NotEqual(0, again.Code);
        Assert.Contains("已确认过", again.Message);
    }

    [Fact]
    public async Task CreateCheckAsync_Confirmed_AppliesImmediately()
    {
        SetStock(ProductAId, 10);
        var r = await StockSvc.CreateCheckAsync(new CreateStockCheckDto
        {
            Status = "已确认",
            Items = new List<StockCheckItemDto> { CheckItem(ProductAId, 10, 12) },
        });
        Assert.Equal(0, r.Code);
        Assert.Equal(12, GetProduct(ProductAId).StockQuantity);   // 盘盈立即生效
    }

    [Fact]
    public async Task CreateCheckAsync_EmptyItems_ReturnsError()
    {
        var r = await StockSvc.CreateCheckAsync(new CreateStockCheckDto { Items = new List<StockCheckItemDto>() });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("盘点明细为空", r.Message);
    }

    [Fact]
    public async Task CheckListAsync_ReturnsCreatedCheck()
    {
        SetStock(ProductAId, 10);
        await StockSvc.CreateCheckAsync(CheckDto(CheckItem(ProductAId, 10, 8)));

        var page = await StockSvc.CheckListAsync(1, 20);
        Assert.Equal(1, page.Total);
        Assert.Equal("草稿", Prop<string>(page.List[0], "status"));
    }

    // ================= 临期商品 =================

    [Fact]
    public async Task ExpiryListAsync_FilterByTag()
    {
        SetStock(ProductBId, 30, 8.00m);
        SeedBatch(ProductBId, DateTime.Today.AddDays(3), 10);    // 7 天内
        SeedBatch(ProductBId, DateTime.Today.AddDays(20), 10);   // 30 天内
        SeedBatch(ProductBId, DateTime.Today.AddDays(-2), 10);   // 已过期

        var within7 = await StockSvc.ExpiryListAsync("7", 1, 50);
        Assert.Equal(1, Prop<int>(within7, "total"));

        var within30 = await StockSvc.ExpiryListAsync("30", 1, 50);
        Assert.Equal(2, Prop<int>(within30, "total"));

        var expired = await StockSvc.ExpiryListAsync("expired", 1, 50);
        Assert.Equal(1, Prop<int>(expired, "total"));
    }

    [Fact]
    public async Task MarkProcessedAsync_DeductsStockAndWritesLog()
    {
        SetStock(ProductBId, 20, 8.00m);
        var batch = SeedBatch(ProductBId, DateTime.Today.AddDays(2), 6);

        var r = await StockSvc.MarkProcessedAsync(new[] { batch.Id });
        Assert.Equal(0, r.Code);

        Assert.Equal(14, GetProduct(ProductBId).StockQuantity);   // 扣减批次数量 6
        var log = LastStockLog(ProductBId)!;
        Assert.Equal("临期报损", log.ChangeType);
        Assert.Equal(-6, log.ChangeQty);

        var after = await Db.Batches.AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        Assert.True(after.IsProcessed);
        Assert.NotNull(after.ProcessedAt);
    }

    [Fact]
    public async Task MarkProcessedAsync_QtyGreaterThanStock_NeverNegative()
    {
        SetStock(ProductBId, 3, 8.00m);                        // 库存只有 3
        var batch = SeedBatch(ProductBId, DateTime.Today.AddDays(2), 10);

        await StockSvc.MarkProcessedAsync(new[] { batch.Id });

        Assert.Equal(0, GetProduct(ProductBId).StockQuantity);  // 最多扣到 0
    }

    [Fact]
    public async Task MarkProcessedAsync_AlreadyProcessed_ReturnsError()
    {
        SetStock(ProductBId, 20, 8.00m);
        var batch = SeedBatch(ProductBId, DateTime.Today.AddDays(2), 6);
        await StockSvc.MarkProcessedAsync(new[] { batch.Id });

        var again = await StockSvc.MarkProcessedAsync(new[] { batch.Id });
        Assert.NotEqual(0, again.Code);
        Assert.Contains("均已处理", again.Message);
    }

    [Fact]
    public async Task MarkProcessedAsync_EmptyIds_ReturnsError()
    {
        var r = await StockSvc.MarkProcessedAsync(Array.Empty<long>());
        Assert.NotEqual(0, r.Code);
        Assert.Contains("请选择要标记的记录", r.Message);
    }

    [Fact]
    public async Task ExpiryListAsync_ProcessedOnly_ShowsProcessedBatch()
    {
        SetStock(ProductBId, 20, 8.00m);
        var batch = SeedBatch(ProductBId, DateTime.Today.AddDays(2), 6);
        await StockSvc.MarkProcessedAsync(new[] { batch.Id });

        var processed = await StockSvc.ExpiryListAsync("processed", 1, 50);
        Assert.Equal(1, Prop<int>(processed, "total"));

        var pending = await StockSvc.ExpiryListAsync(null, 1, 50);
        Assert.Equal(0, Prop<int>(pending, "total"));   // 未处理页不再显示
    }

    // ================= 库存预警 =================

    [Fact]
    public async Task WarningsAsync_ReturnsLowStockProducts()
    {
        SetStock(ProductAId, 2, 5.00m);
        var pa = Db.Products.First(p => p.Id == ProductAId);
        pa.SafetyStock = 10;
        await Db.SaveChangesAsync();

        var list = await StockSvc.WarningsAsync();
        var a = list.FirstOrDefault(x => Prop<int>(x, "id") == ProductAId);
        Assert.NotNull(a);
        Assert.Equal("预警", Prop<string>(a, "status"));
        Assert.Equal(8m, Prop<decimal>(a, "shortage"));   // 建议补货 = 安全库存 - 库存
    }

    [Fact]
    public async Task WarningsAsync_ZeroStock_StatusIsOutOfStock()
    {
        var list = await StockSvc.WarningsAsync();
        Assert.Contains(list, x => Prop<string>(x, "status") == "缺货");
    }

    // ================= 库存流水 =================

    [Fact]
    public async Task LogListAsync_ReturnsLogsWithProductName()
    {
        SetStock(ProductAId, 10, 5.00m);
        await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = SupplierId,
            Details = new List<PurchaseDetailDto> { new() { ProductId = ProductAId, Qty = 5, CostPrice = 4 } },
        });

        var page = await StockSvc.LogListAsync(null, null, 1, 20);
        Assert.Equal(1, page.Total);
        Assert.Equal("采购入库", Prop<string>(page.List[0], "changeType"));
        Assert.Equal("商品A（无有效期）", Prop<string>(page.List[0], "productName"));
    }

    [Fact]
    public async Task LogListAsync_FilterByChangeType()
    {
        SetStock(ProductAId, 10, 5.00m);
        await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = SupplierId,
            Details = new List<PurchaseDetailDto> { new() { ProductId = ProductAId, Qty = 5, CostPrice = 4 } },
        });

        var matched = await StockSvc.LogListAsync(null, "采购入库", 1, 20);
        Assert.Equal(1, matched.Total);

        var none = await StockSvc.LogListAsync(null, "销售出库", 1, 20);
        Assert.Equal(0, none.Total);
    }

    [Fact]
    public async Task ExportLogAsync_ReturnsAllRows()
    {
        SetStock(ProductAId, 10, 5.00m);
        await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = SupplierId,
            Details = new List<PurchaseDetailDto> { new() { ProductId = ProductAId, Qty = 5, CostPrice = 4 } },
        });

        var rows = await StockSvc.ExportLogAsync(null, null);
        Assert.Single(rows);
        Assert.Equal("采购入库", rows[0]["changeType"]);
        Assert.Equal(5m, rows[0]["changeQty"]);
    }

    [Fact]
    public async Task ExportInventoryAsync_ReturnsAllProducts()
    {
        var rows = await StockSvc.ExportInventoryAsync(null, null);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (string)r["barcode"]! == "A001");
    }
}
