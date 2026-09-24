using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 报表 5 个接口（<c>/api/v1/reports/*</c>）的上线前集成测试。
///
/// 这一组的价值不在于「路由通不通」，而在于**口径的自洽性**：报表是唯一会
/// 「同一件事用好几条路径各算一遍」的地方，口径只要有一处漂移，页面上的数字就会
/// 互相矛盾，而单测是照着实现写的、跟着一起漂，看不出来。所以这里断言的都是
/// **响应体内部/响应体之间的恒等式**（合计＝逐行之和、avg＝sales/orders、
/// 分类合计＝总额），换口径实现也仍然成立、写错了就会红。
///
/// 另一件事：<c>ReportsController</c> 五个端点都还是 <c>ApiResult&lt;object&gt;</c>，
/// 响应字段名（camelCase 键）就是前端的契约本身 —— 这里逐字断言，给「响应侧类型契约」
/// 那批重构（见项目笔记）留一道回归网。
/// </summary>
public class ReportsIntegrationTests : IntegrationSeedBase
{
    public ReportsIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    // ================= 日销售报表 =================

    [Fact]
    public async Task 日报表_字段齐全且合计与趋势明细自洽()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITRPT");
        var productId = await SeedProductAsync(name, salePrice: 12m, costPrice: 6m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 10m, 5m));
        await SeedSaleAsync((productId, 3m, 12m));

        var body = await ReadBody(await Client.GetAsync($"/api/v1/reports/daily-sales?date={Today}"));

        // 响应形状：summary / trend / top5 / items 四个键，全小写开头（线上就是这个名字）
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.True(data.TryGetProperty("summary", out var summary), "缺少 summary");
        Assert.True(data.TryGetProperty("trend", out var trend), "缺少 trend");
        Assert.True(data.TryGetProperty("top5", out var top5), "缺少 top5");
        Assert.True(data.TryGetProperty("items", out var items), "缺少 items");

        var sales = summary.GetProperty("sales").GetDecimal();
        var orders = summary.GetProperty("orders").GetInt32();
        var avg = summary.GetProperty("avg").GetDecimal();

        Assert.True(orders >= 1, "本用例刚成交了一单，订单数应至少为 1");
        Assert.True(sales >= 36m, $"销售额应包含本用例的 36 元，实际 {sales}");
        // 恒等式：客单价 = 销售额 / 订单数
        Assert.Equal(Math.Round(sales / orders, 2), avg);

        // 近 7 天趋势的最后一天就是今天，其销售额必须等于 summary.sales
        Assert.Equal(7, trend.GetArrayLength());
        Assert.Equal(sales, trend[6].GetProperty("sales").GetDecimal());

        // 当日 Top5 与当日明细都应包含本用例刚卖出的商品（名称唯一，分组里只有它）
        var topRow = top5.EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        Assert.Equal(3m, topRow.GetProperty("qty").GetDecimal());
        Assert.Equal(36m, topRow.GetProperty("amount").GetDecimal());

        var itemRow = items.EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        Assert.Equal(3m, itemRow.GetProperty("qty").GetDecimal());
        Assert.Equal(36m, itemRow.GetProperty("amount").GetDecimal());
        // 卖价 12 > 移动加权成本 5 ⇒ 该行毛利必须为正且小于销售额
        var profit = itemRow.GetProperty("profit").GetDecimal();
        Assert.InRange(profit, 0.01m, 35.99m);
    }

    // ================= 月销售报表 =================

    [Fact]
    public async Task 月报表_按日汇总且当天那一天有数()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITRPTM");
        var productId = await SeedProductAsync(name, salePrice: 8m, costPrice: 4m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 5m, 4m));
        await SeedSaleAsync((productId, 2m, 8m));   // 16 元

        var month = DateTime.Today.ToString("yyyy-MM");
        var body = await ReadBody(await Client.GetAsync($"/api/v1/reports/monthly-sales?month={month}"));

        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.True(data.TryGetProperty("total", out var total));
        Assert.True(data.TryGetProperty("received", out _));
        Assert.True(data.TryGetProperty("refund", out _));
        Assert.True(data.TryGetProperty("profit", out _));

        var days = data.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month), days.Count);

        // 种子的销售单都在 8 月，本月只有本用例刚成交的一单 ⇒ 只有今天那天应大于 0
        var todayRow = days[DateTime.Today.Day - 1];
        Assert.Equal($"{DateTime.Today.Month:D2}-{DateTime.Today.Day:D2}", todayRow.GetProperty("date").GetString());
        Assert.Equal(16m, todayRow.GetProperty("sales").GetDecimal());

        Assert.True(total.GetDecimal() >= 16m, $"月度合计应包含本用例的 16 元，实际 {total.GetDecimal()}");
        var expectedOther = days.Where((_, i) => i != DateTime.Today.Day - 1).Sum(d => d.GetProperty("sales").GetDecimal());
        Assert.Equal(0m, expectedOther);
    }

    [Fact]
    public async Task 月报表_非法月份回退到本月而不是报错()
    {
        await LoginAsAdminAsync();
        var now = DateTime.Today;
        var bad = await ReadBody(await Client.GetAsync("/api/v1/reports/monthly-sales?month=2026-13"));
        var good = await ReadBody(await Client.GetAsync($"/api/v1/reports/monthly-sales?month={now:yyyy-MM}"));

        Assert.Equal(0, bad.GetProperty("code").GetInt32());
        Assert.Equal(
            good.GetProperty("data").GetProperty("days").GetArrayLength(),
            bad.GetProperty("data").GetProperty("days").GetArrayLength());
    }

    // ================= 利润分析 =================

    [Fact]
    public async Task 毛利分析_毛利等于收入减成本且分类合计等于总额()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITRPTF");
        var productId = await SeedProductAsync(name, salePrice: 20m, costPrice: 12m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 10m, 10m));   // 移动加权成本 = 10
        await SeedSaleAsync((productId, 4m, 20m));                    // 80 元

        var body = await ReadBody(await Client.GetAsync(
            $"/api/v1/reports/profit?dateFrom={Today}&dateTo={Today}"));

        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        var sales = data.GetProperty("sales").GetDecimal();
        var cost = data.GetProperty("cost").GetDecimal();
        var profit = data.GetProperty("profit").GetDecimal();
        var rate = data.GetProperty("profitRate").GetDecimal();

        Assert.True(sales >= 80m, $"收入应包含本用例的 80 元，实际 {sales}");
        // 核心恒等式：毛利 = 收入 − 成本（本用例与同类其他用例都是零让利单，退货净额为 0）
        Assert.Equal(sales - cost, profit);
        Assert.Equal(sales == 0 ? 0 : Math.Round(profit / sales, 4), rate);
        Assert.True(cost > 0, "成本应大于 0（进货已入库，明细里带成本快照）");

        // 分类占比的销售额合计必须等于总额 —— 报表最容易被改坏的地方就是这里
        var byCategory = data.GetProperty("byCategory").EnumerateArray().ToList();
        Assert.NotEmpty(byCategory);
        Assert.Equal(sales, byCategory.Sum(x => x.GetProperty("sales").GetDecimal()));
    }

    // ================= 供应商对账单 =================

    [Fact]
    public async Task 供应商对账_逐行求和等于净应付且退货记负()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITRPTS"), salePrice: 9m, costPrice: 5m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));

        var purchase = await SeedPurchaseAsync(supplierId, (productId, 20m, 5m));   // 100
        var purchaseOrderNo = purchase.GetProperty("orderNo").GetString()!;

        // 退 3 件（15 元），此时库存 17
        var returnResp = await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 3m, costPrice = 5m } },
            returnTotal = 15m,
            reason = "对账用例",
        });
        var returnOrderNo = (await ExpectOk(returnResp)).GetProperty("orderNo").GetString()!;

        var body = await ReadBody(await Client.GetAsync($"/api/v1/reports/supplier-statement?supplierId={supplierId}"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");

        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(2, data.GetProperty("count").GetInt32());
        Assert.Equal(1, data.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(1, data.GetProperty("returnCount").GetInt32());

        Assert.Equal(100m, data.GetProperty("purchaseTotal").GetDecimal());
        Assert.Equal(15m, data.GetProperty("returnTotal").GetDecimal());
        Assert.Equal(85m, data.GetProperty("netPayable").GetDecimal());

        // 进货记正、退货记负
        var purchaseRow = items.First(x => x.GetProperty("orderNo").GetString() == purchaseOrderNo);
        Assert.Equal("进货", purchaseRow.GetProperty("type").GetString());
        Assert.Equal(100m, purchaseRow.GetProperty("amount").GetDecimal());
        Assert.Equal(20m, purchaseRow.GetProperty("qty").GetDecimal());

        var returnRow = items.First(x => x.GetProperty("orderNo").GetString() == returnOrderNo);
        Assert.Equal("退货", returnRow.GetProperty("type").GetString());
        Assert.Equal(-15m, returnRow.GetProperty("amount").GetDecimal());
        Assert.Equal(3m, returnRow.GetProperty("qty").GetDecimal());

        // ★ 对账单最该一眼看懂的事：屏幕上每一行加起来 = 最后那个净应付
        Assert.Equal(data.GetProperty("netPayable").GetDecimal(),
                     items.Sum(x => x.GetProperty("amount").GetDecimal()));
    }

    [Fact]
    public async Task 供应商对账_作废的进货单与退货单都不进流水()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITRPTV"), salePrice: 9m, costPrice: 5m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));

        // 两张进货单：A 作废、B 保留。作废必须是「这一张不算」，不能把整个查询带坏。
        var a = await SeedPurchaseAsync(supplierId, (productId, 10m, 5m));
        await ExpectOk(await PostAsync($"/api/v1/purchases/{a.GetProperty("id").GetInt32()}/void"));
        var b = await SeedPurchaseAsync(supplierId, (productId, 10m, 5m));

        // 一张退货单：作废后应回补库存且不进对账
        var returnResp = await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 2m, costPrice = 5m } },
            returnTotal = 10m,
        });
        var returnData = await ExpectOk(returnResp);
        var returnOrderNo = returnData.GetProperty("orderNo").GetString()!;
        await ExpectOk(await PostAsync($"/api/v1/purchase-returns/{returnData.GetProperty("id").GetInt32()}/void"));

        var body = await ReadBody(await Client.GetAsync($"/api/v1/reports/supplier-statement?supplierId={supplierId}"));
        var data = body.GetProperty("data");
        var items = data.GetProperty("items").EnumerateArray().ToList();

        // 只剩 B 一张进货单
        Assert.Single(items);
        Assert.Equal(b.GetProperty("orderNo").GetString(), items[0].GetProperty("orderNo").GetString());
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.Equal(1, data.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(0, data.GetProperty("returnCount").GetInt32());
        Assert.Equal(50m, data.GetProperty("purchaseTotal").GetDecimal());
        Assert.Equal(0m, data.GetProperty("returnTotal").GetDecimal());
        Assert.Equal(50m, data.GetProperty("netPayable").GetDecimal());
        Assert.DoesNotContain(items, x =>
            x.GetProperty("orderNo").GetString() == a.GetProperty("orderNo").GetString()
            || x.GetProperty("orderNo").GetString() == returnOrderNo);
    }

    [Fact]
    public async Task 供应商对账_未给供应商时返回空流水而不是500()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await Client.GetAsync("/api/v1/reports/supplier-statement?supplierId=999999"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        Assert.Empty(body.GetProperty("data").GetProperty("items").EnumerateArray());
    }

    // ================= 赊账汇总 =================

    [Fact]
    public async Task 赊账汇总_按微信号聚合且各项合计等于逐行之和()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITRPTC");
        var productId = await SeedProductAsync(name, salePrice: 30m, costPrice: 20m, stock: 10m);
        var wechatId = NewTag("ITWX");

        var saleResp = await PostJsonAsync("/api/v1/sales", new
        {
            items = new[] { new { productId, qty = 2m, unitPrice = 30m, subTotal = 60m } },
            totalAmount = 60m,
            discountAmount = 0m,
            payAmount = 60m,
            payMethod = "赊账",
            isCredit = true,
            wechatId,
        });
        await ExpectOk(saleResp);

        var body = await ReadBody(await Client.GetAsync("/api/v1/reports/credit-summary"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");

        var rows = data.GetProperty("byWechat").EnumerateArray().ToList();
        var mine = rows.First(x => x.GetProperty("wechatId").GetString() == wechatId);
        Assert.Equal(60m, mine.GetProperty("creditTotal").GetDecimal());
        Assert.Equal(0m, mine.GetProperty("paidTotal").GetDecimal());
        Assert.Equal(60m, mine.GetProperty("remainingTotal").GetDecimal());
        Assert.Equal(1, mine.GetProperty("unsettledCount").GetInt32());

        // 恒等式：明细逐行相加 = 顶部三个合计
        Assert.Equal(data.GetProperty("totalCredit").GetDecimal(),
                     rows.Sum(x => x.GetProperty("creditTotal").GetDecimal()));
        Assert.Equal(data.GetProperty("paidCredit").GetDecimal(),
                     rows.Sum(x => x.GetProperty("paidTotal").GetDecimal()));
        Assert.Equal(data.GetProperty("unpaidCredit").GetDecimal(),
                     rows.Sum(x => x.GetProperty("remainingTotal").GetDecimal()));

        // 顶部笔数（「赊账汇总」卡片直接绑这两个字段）
        // ① 未结清笔数 = 各微信号未结清笔数之和
        var unsettled = data.GetProperty("unsettled").GetInt32();
        var settled = data.GetProperty("settled").GetInt32();
        Assert.Equal(rows.Sum(x => x.GetProperty("unsettledCount").GetInt32()), unsettled);
        Assert.True(unsettled >= 1);   // 至少含刚创建的这笔

        // ② 与「赊账管理」页统计同源，两页不该给出不同笔数；一笔也不会凭空多出或漏掉
        var credits = (await ReadBody(await Client.GetAsync("/api/v1/credits?pageSize=1"))).GetProperty("data");
        var creditStats = credits.GetProperty("stats");
        Assert.Equal(creditStats.GetProperty("unsettled").GetInt32(), unsettled);
        Assert.Equal(creditStats.GetProperty("settled").GetInt32(), settled);
        Assert.Equal(credits.GetProperty("total").GetInt32(), unsettled + settled);
    }

    // ================= 看板（与报表同源，一并守） =================

    [Fact]
    public async Task 看板汇总_今日块与报表口径一致()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITDSH");
        // 安全库存 99 是故意的：这件商品在进货后仍会落进「缺货/预警」里，让 stock 块有真实数字
        var productId = await SeedProductAsync(name, salePrice: 15m, costPrice: 9m, safetyStock: 99m);
        // 先入库再卖 —— 库存为 0 的商品卖不出去（收银会因库存不足整单失败）
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 5m, 9m));
        await SeedSaleAsync((productId, 1m, 15m));

        var dash = await ReadBody(await Client.GetAsync("/api/v1/dashboard/summary"));
        Assert.Equal(0, dash.GetProperty("code").GetInt32());
        var data = dash.GetProperty("data");
        Assert.True(data.TryGetProperty("today", out var today));
        Assert.True(data.TryGetProperty("month", out _));
        Assert.True(data.TryGetProperty("stock", out var stock));
        Assert.True(data.TryGetProperty("credit", out var credit));
        Assert.True(data.TryGetProperty("trend", out var trend));
        Assert.True(data.TryGetProperty("top5", out _));

        Assert.True(today.GetProperty("sales").GetDecimal() >= 15m);
        Assert.True(today.GetProperty("orders").GetInt32() >= 1);
        Assert.Equal(7, trend.GetArrayLength());
        // stock/credit 两块都是聚合数，至少得是形状正确的非负数
        Assert.True(stock.GetProperty("shortage").GetInt32() >= 0);
        Assert.True(stock.GetProperty("expiry").GetInt32() >= 0);
        Assert.True(credit.GetProperty("total").GetDecimal() >= 0m);
        Assert.True(credit.GetProperty("unsettled").GetInt32() >= 0);

        // 看板与日报表的「今日营业额」必须同源同值（两处各算一遍，最容易漂的就是这里）
        var daily = await ReadBody(await Client.GetAsync($"/api/v1/reports/daily-sales?date={Today}"));
        Assert.Equal(daily.GetProperty("data").GetProperty("summary").GetProperty("sales").GetDecimal(),
                     today.GetProperty("sales").GetDecimal());
    }
}
