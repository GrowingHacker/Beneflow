using System.Text.Json;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>报表服务测试：日/月销售、利润分析、供应商对账、赊账汇总、临期阈值、首页看板</summary>
public class ReportServiceTests : TestBase
{
    private static DateTime Today => DateTime.Today;

    /// <summary>直接落库一张进货单（报表只读，不依赖采购流程）</summary>
    private PurchaseOrder SeedPurchase(decimal amount, decimal qty, DateTime at, int? supplierId = null)
    {
        var o = new PurchaseOrder
        {
            OrderNo = $"PO{at:yyyyMMdd}{Db.PurchaseOrders.Count() + 1:D3}",
            SupplierId = supplierId ?? SupplierId,
            TotalQty = qty,
            TotalAmount = amount,
            CreatedBy = UserId,
            CreatedAt = at,
        };
        Db.PurchaseOrders.Add(o);
        Db.SaveChanges();
        return o;
    }

    /// <summary>直接落库一条赊账记录</summary>
    private CreditSale SeedCredit(string wechat, decimal credit, decimal paid, DateTime at, bool settled = false)
    {
        var o = SeedSale(credit, at, isCredit: true, payMethod: "赊账");
        var c = new CreditSale
        {
            SaleOrderId = o.Id,
            WechatId = wechat,
            CreditAmount = credit,
            PaidAmount = paid,
            RemainingAmount = credit - paid,
            Status = settled,
            CreatedAt = at,
        };
        Db.CreditSales.Add(c);
        Db.SaveChanges();
        return c;
    }

    private void SeedBatch(int productId, DateTime expireDate, decimal qty = 10, bool processed = false)
    {
        Db.Batches.Add(new ProductBatch
        {
            ProductId = productId,
            BatchNo = $"B{Db.Batches.Count() + 1:D3}",
            ExpireDate = expireDate,
            Quantity = qty,
            IsProcessed = processed,
        });
        Db.SaveChanges();
    }

    /// <summary>读取匿名对象属性（报表一律返回匿名对象）</summary>
    private static T? P<T>(object? obj, string name) => Prop<T>(obj, name);

    // ==================== 日销售报表 ====================

    [Fact]
    public async Task DailySalesAsync_NoOrders_ReturnsZeros()
    {
        var r = await ReportSvc.DailySalesAsync(Today);

        var summary = P<object>(r, "summary");
        Assert.Equal(0m, P<decimal>(summary, "sales"));
        Assert.Equal(0, P<int>(summary, "orders"));
        Assert.Equal(0m, P<decimal>(summary, "avg"));
        Assert.Equal(0m, P<decimal>(summary, "profit"));
        Assert.Empty(P<IEnumerable<object>>(r, "items")!);
    }

    [Fact]
    public async Task DailySalesAsync_SumsSalesOrdersAvgAndProfit()
    {
        var o1 = SeedSale(100m, Today.AddHours(9), discount: 10m);
        SeedSaleDetail(o1, ProductAId, 5, 20m, 12m);          // 小计 100，成本 60

        var o2 = SeedSale(50m, Today.AddHours(15));
        SeedSaleDetail(o2, ProductBId, 2, 25m, 10m);          // 小计 50，成本 20

        var r = await ReportSvc.DailySalesAsync(Today);
        var summary = P<object>(r, "summary");

        Assert.Equal(150m, P<decimal>(summary, "sales"));     // 100 + 50
        Assert.Equal(2, P<int>(summary, "orders"));
        Assert.Equal(75m, P<decimal>(summary, "avg"));        // 150 / 2
        // 毛利 = (100-60) + (50-20) - 优惠 10 = 60
        Assert.Equal(60m, P<decimal>(summary, "profit"));
    }

    [Fact]
    public async Task DailySalesAsync_ExcludesOtherDays()
    {
        var yesterday = SeedSale(999m, Today.AddDays(-1));
        SeedSaleDetail(yesterday, ProductAId, 1, 999m, 1m);

        var today = SeedSale(30m, Today.AddHours(10));
        SeedSaleDetail(today, ProductAId, 3, 10m, 4m);

        var r = await ReportSvc.DailySalesAsync(Today);

        Assert.Equal(30m, P<decimal>(P<object>(r, "summary"), "sales"));
        Assert.Single(P<IEnumerable<object>>(r, "items")!);
    }

    [Fact]
    public async Task DailySalesAsync_Trend_HasSevenDays()
    {
        SeedSale(10m, Today.AddDays(-6));
        SeedSale(20m, Today);

        var r = await ReportSvc.DailySalesAsync(Today);
        var trend = P<IEnumerable<object>>(r, "trend")!.ToList();

        Assert.Equal(7, trend.Count);
        Assert.Equal(Today.AddDays(-6).ToString("MM-dd"), P<string>(trend[0], "date"));
        Assert.Equal(10m, P<decimal>(trend[0], "sales"));
        Assert.Equal(20m, P<decimal>(trend[6], "sales"));
    }

    [Fact]
    public async Task DailySalesAsync_Top5_SortedByAmountAndCapped()
    {
        // Top5 按商品名聚合：需要 6 个不同名称的商品才能验证截断
        var o = SeedSale(1000m, Today.AddHours(8));
        for (var i = 1; i <= 6; i++)
        {
            var p = new Product
            {
                Name = $"Top商品{i}", Barcode = $"T{i:D3}", CategoryId = CategoryId, Unit = "个",
                SalePrice = 10m * i, CreatedAt = DateTime.Now,
            };
            Db.Products.Add(p);
            Db.SaveChanges();
            SeedSaleDetail(o, p.Id, 1, 10m * i, 1m);           // 销售额 10/20/…/60
        }

        var r = await ReportSvc.DailySalesAsync(Today);
        var top5 = P<IEnumerable<object>>(r, "top5")!.ToList();

        Assert.Equal(5, top5.Count);
        Assert.Equal("Top商品6", P<string>(top5[0], "name"));  // 销售额最高排最前
        Assert.Equal(60m, P<decimal>(top5[0], "amount"));
        Assert.Equal(20m, P<decimal>(top5[4], "amount"));      // 最低的 Top商品1 被挤出
    }

    // ==================== 月销售报表 ====================

    [Fact]
    public async Task MonthlySalesAsync_GroupsByDay()
    {
        var start = new DateTime(Today.Year, Today.Month, 1);
        var day3 = start.AddDays(2);

        var o1 = SeedSale(120m, day3.AddHours(9));
        SeedSaleDetail(o1, ProductAId, 10, 12m, 5m);           // 成本 50
        var o2 = SeedSale(80m, day3.AddHours(20), discount: 5m);
        SeedSaleDetail(o2, ProductBId, 4, 20m, 10m);           // 成本 40
        SeedSale(500m, start.AddMonths(1));                    // 次月，不计入

        var r = await ReportSvc.MonthlySalesAsync(Today.Year, Today.Month);

        Assert.Equal(200m, P<decimal>(r, "total"));
        Assert.Equal(2, P<int>(r, "orders"));
        // 毛利 = 200 - 90 - 5 = 105
        Assert.Equal(105m, P<decimal>(r, "profit"));

        var days = P<IEnumerable<object>>(r, "days")!.ToList();
        Assert.Equal(DateTime.DaysInMonth(Today.Year, Today.Month), days.Count);
        Assert.Equal(200m, P<decimal>(days[2], "sales"));
        Assert.Equal(0m, P<decimal>(days[0], "sales"));
    }

    [Fact]
    public async Task MonthlySalesAsync_EmptyMonth_ReturnsZeros()
    {
        var r = await ReportSvc.MonthlySalesAsync(2020, 2);

        Assert.Equal(0m, P<decimal>(r, "total"));
        Assert.Equal(0, P<int>(r, "orders"));
        Assert.Equal(DateTime.DaysInMonth(2020, 2), P<IEnumerable<object>>(r, "days")!.Count());
    }

    // ==================== 利润分析 ====================

    [Fact]
    public async Task ProfitAnalysisAsync_CalculatesProfitRateAndCategory()
    {
        var o = SeedSale(300m, Today.AddHours(10), discount: 20m);
        SeedSaleDetail(o, ProductAId, 10, 30m, 10m);           // 小计 300，成本 100

        var r = await ReportSvc.ProfitAnalysisAsync(Today, Today);

        Assert.Equal(300m, P<decimal>(r, "sales"));
        Assert.Equal(100m, P<decimal>(r, "cost"));
        Assert.Equal(180m, P<decimal>(r, "profit"));           // 300 - 100 - 20
        Assert.Equal(0.6m, P<decimal>(r, "profitRate"));       // 180 / 300

        var cats = P<IEnumerable<object>>(r, "byCategory")!.ToList();
        Assert.Single(cats);
        Assert.Equal("测试分类", P<string>(cats[0], "category"));
        Assert.Equal(300m, P<decimal>(cats[0], "sales"));
        Assert.Equal(200m, P<decimal>(cats[0], "profit"));     // 分类口径不扣订单优惠
    }

    [Fact]
    public async Task ProfitAnalysisAsync_NoSales_ProfitRateZero()
    {
        var r = await ReportSvc.ProfitAnalysisAsync(Today, Today);

        Assert.Equal(0m, P<decimal>(r, "sales"));
        Assert.Equal(0m, P<decimal>(r, "profitRate"));
    }

    // ==================== 供应商对账单 ====================

    [Fact]
    public async Task SupplierStatementAsync_ListsOrdersAndTotals()
    {
        SeedPurchase(1000m, 100, Today.AddDays(-3));
        SeedPurchase(500m, 50, Today.AddDays(-1));

        var r = await ReportSvc.SupplierStatementAsync(SupplierId, null, null);
        var items = P<IEnumerable<object>>(r, "items")!.ToList();

        Assert.Equal(2, P<int>(r, "count"));
        Assert.Equal(1500m, P<decimal>(r, "total"));
        Assert.Equal("测试供应商", P<string>(items[0], "supplierName"));   // 按时间倒序
    }

    [Fact]
    public async Task SupplierStatementAsync_FiltersByDateRange()
    {
        SeedPurchase(1000m, 100, Today.AddDays(-10));
        SeedPurchase(500m, 50, Today);

        var r = await ReportSvc.SupplierStatementAsync(SupplierId, Today.AddDays(-1).ToString("yyyy-MM-dd"), null);
        var items = P<IEnumerable<object>>(r, "items")!.ToList();

        Assert.Single(items);
        Assert.Equal(500m, P<decimal>(r, "total"));
    }

    [Fact]
    public async Task SupplierStatementAsync_OtherSupplier_Excluded()
    {
        var other = new Supplier { Name = "另一家供应商" };
        Db.Suppliers.Add(other);
        Db.SaveChanges();
        SeedPurchase(800m, 8, Today, other.Id);

        var r = await ReportSvc.SupplierStatementAsync(SupplierId, null, null);

        Assert.Equal(0, P<int>(r, "count"));
    }

    // ==================== 赊账汇总 ====================

    [Fact]
    public async Task CreditSummaryAsync_GroupsByWechat()
    {
        SeedCredit("wx_a", 100m, 40m, Today.AddDays(-2));      // 剩 60，未结清
        SeedCredit("wx_a", 50m, 0m, Today.AddDays(-1));        // 剩 50，未结清
        SeedCredit("wx_b", 200m, 200m, Today, settled: true);  // 已结清

        var r = await ReportSvc.CreditSummaryAsync(null, null);
        var byWechat = P<IEnumerable<object>>(r, "byWechat")!.ToList();

        Assert.Equal(2, byWechat.Count);
        Assert.Equal("wx_a", P<string>(byWechat[0], "wechatId"));   // 剩余最多排前
        Assert.Equal(150m, P<decimal>(byWechat[0], "creditTotal"));
        Assert.Equal(110m, P<decimal>(byWechat[0], "remainingTotal"));
        Assert.Equal(2, P<int>(byWechat[0], "unsettledCount"));

        Assert.Equal(350m, P<decimal>(r, "totalCredit"));
        Assert.Equal(240m, P<decimal>(r, "paidCredit"));
        Assert.Equal(110m, P<decimal>(r, "unpaidCredit"));
    }

    [Fact]
    public async Task CreditSummaryAsync_DateFilter_Applied()
    {
        SeedCredit("wx_old", 100m, 0m, Today.AddDays(-30));
        SeedCredit("wx_new", 60m, 0m, Today);

        var r = await ReportSvc.CreditSummaryAsync(Today.AddDays(-1).ToString("yyyy-MM-dd"), null);

        Assert.Equal(60m, P<decimal>(r, "totalCredit"));
        Assert.Single(P<IEnumerable<object>>(r, "byWechat")!);
    }

    [Fact]
    public async Task CreditSummaryAsync_NoData_ReturnsZeros()
    {
        var r = await ReportSvc.CreditSummaryAsync(null, null);

        Assert.Equal(0m, P<decimal>(r, "totalCredit"));
        Assert.Empty(P<IEnumerable<object>>(r, "byWechat")!);
    }

    // ==================== 临期阈值 ====================

    [Fact]
    public async Task GetExpiryDaysAsync_NoConfig_ReturnsDefault30() =>
        Assert.Equal(30, await ReportSvc.GetExpiryDaysAsync());

    [Fact]
    public async Task GetExpiryDaysAsync_ReadsStockConfig()
    {
        SetConfig("stock", """{"warningThreshold":5,"expiryDays":7}""");

        Assert.Equal(7, await ReportSvc.GetExpiryDaysAsync());
    }

    [Fact]
    public async Task GetExpiryDaysAsync_BadJson_FallsBackTo30()
    {
        SetConfig("stock", "{ this is not json");

        Assert.Equal(30, await ReportSvc.GetExpiryDaysAsync());
    }

    // ==================== 首页看板 ====================

    [Fact]
    public async Task DashboardSummaryAsync_ExcludesVoidedOrders()
    {
        var ok = SeedSale(100m, Today.AddHours(9));
        SeedSaleDetail(ok, ProductAId, 5, 20m, 8m);            // 小计 100，成本 40
        var voided = SeedSale(999m, Today.AddHours(10), isVoided: true);
        SeedSaleDetail(voided, ProductAId, 1, 999m, 1m);

        var r = await ReportSvc.DashboardSummaryAsync();
        var today = P<object>(r, "today");

        Assert.Equal(100m, P<decimal>(today, "sales"));
        Assert.Equal(1, P<int>(today, "orders"));
        Assert.Equal(100m, P<decimal>(today, "avg"));
        Assert.Equal(60m, P<decimal>(today, "profit"));        // 100 - 40
    }

    [Fact]
    public async Task DashboardSummaryAsync_MonthAggregation()
    {
        var monthStart = new DateTime(Today.Year, Today.Month, 1);
        var o = SeedSale(200m, monthStart.AddHours(9), discount: 10m);
        SeedSaleDetail(o, ProductAId, 10, 20m, 5m);            // 成本 50
        SeedSale(5000m, monthStart.AddMonths(-1));             // 上月不计入

        var r = await ReportSvc.DashboardSummaryAsync();
        var month = P<object>(r, "month");

        Assert.Equal(200m, P<decimal>(month, "sales"));
        Assert.Equal(1, P<int>(month, "orders"));
        Assert.Equal(140m, P<decimal>(month, "profit"));       // 200 - 50 - 10
    }

    [Fact]
    public async Task DashboardSummaryAsync_StockWarningCards()
    {
        SetStock(ProductAId, 0, 5m);                           // 缺货
        var p = Db.Products.First(x => x.Id == ProductBId);
        p.StockQuantity = 3;
        p.SafetyStock = 5;                                     // 预警：0 < 3 <= 5
        Db.SaveChanges();

        SeedBatch(ProductAId, Today.AddDays(10), 10);          // 30 天内临期
        SeedBatch(ProductAId, Today.AddDays(-1), 10);          // 已过期
        SeedBatch(ProductBId, Today.AddDays(100), 10);         // 超出阈值
        SeedBatch(ProductBId, Today.AddDays(5), 10, processed: true);   // 已处理，不计

        var r = await ReportSvc.DashboardSummaryAsync();
        var stock = P<object>(r, "stock");

        Assert.Equal(1, P<int>(stock, "shortage"));
        Assert.Equal(1, P<int>(stock, "warning"));
        Assert.Equal(2, P<int>(stock, "expiry"));              // 临期 1 + 过期 1
    }

    [Fact]
    public async Task DashboardSummaryAsync_ExpiryThresholdRespected()
    {
        SeedBatch(ProductAId, Today.AddDays(40), 10);          // 默认 30 天外

        Assert.Equal(0, P<int>(P<object>(await ReportSvc.DashboardSummaryAsync(), "stock"), "expiry"));
        Assert.Equal(1, P<int>(P<object>(await ReportSvc.DashboardSummaryAsync(60), "stock"), "expiry"));
    }

    [Fact]
    public async Task DashboardSummaryAsync_CreditReminder()
    {
        SeedCredit("wx_a", 100m, 30m, Today);                  // 剩 70，未结清
        SeedCredit("wx_b", 50m, 50m, Today, settled: true);    // 已结清

        var r = await ReportSvc.DashboardSummaryAsync();
        var credit = P<object>(r, "credit");

        Assert.Equal(70m, P<decimal>(credit, "total"));
        Assert.Equal(1, P<int>(credit, "unsettled"));
    }

    [Fact]
    public async Task DashboardSummaryAsync_TrendAndTop5()
    {
        var o = SeedSale(300m, Today.AddHours(9));
        SeedSaleDetail(o, ProductAId, 10, 30m, 10m);
        var o2 = SeedSale(90m, Today.AddDays(-40).AddHours(9)); // 超出 30 天窗口，不入 Top5
        SeedSaleDetail(o2, ProductBId, 3, 30m, 10m);

        var r = await ReportSvc.DashboardSummaryAsync();

        Assert.Equal(7, P<IEnumerable<object>>(r, "trend")!.Count());
        var top5 = P<IEnumerable<object>>(r, "top5")!.ToList();
        Assert.Single(top5);
        Assert.Equal(GetProduct(ProductAId).Name, P<string>(top5[0], "name"));
        Assert.Equal(300m, P<decimal>(top5[0], "amount"));
    }

    [Fact]
    public async Task DashboardSummaryAsync_EmptyDb_AllZeros()
    {
        var r = await ReportSvc.DashboardSummaryAsync();

        Assert.Equal(0m, P<decimal>(P<object>(r, "today"), "sales"));
        Assert.Equal(0m, P<decimal>(P<object>(r, "month"), "sales"));
        Assert.Equal(0m, P<decimal>(P<object>(r, "credit"), "total"));
    }

    [Fact]
    public async Task ReportService_QueriesDoNotTrackEntities()
    {
        SeedSale(10m, Today.AddHours(9));
        Db.ChangeTracker.Clear();                             // 清掉播种时挂上的跟踪

        _ = await ReportSvc.DailySalesAsync(Today);
        _ = await ReportSvc.DashboardSummaryAsync();

        Assert.Empty(Db.ChangeTracker.Entries());
    }
}
