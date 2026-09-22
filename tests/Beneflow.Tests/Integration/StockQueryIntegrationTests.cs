using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 库存查询类端点（实时库存 / 临期 / 库存预警 / 库存流水）的上线前集成测试。
///
/// 这几个端点是「只读的」，看起来最不值得测 —— 但它们共同依赖一套**派生状态**：
/// 商品状态不是存下来的，而是每次按 (库存, 安全库存, 未处理批次的最早到期日, 系统设置的临期天数)
/// 现算出来的（<c>StockService.InventoryListAsync</c>）。派生规则一改，页面上的状态标签、
/// 看板的临期数量、导出的筛选结果会**同时**漂移，而每一处的单测都只照着自己那份实现写。
///
/// 因此这里用真实进货（带生产日期）把「还剩几天到期」精确造出来，再断言各档位分桶正确。
/// </summary>
public class StockQueryIntegrationTests : IntegrationSeedBase
{
    public StockQueryIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    // ================= 实时库存 =================

    [Fact]
    public async Task 实时库存_关键词命中且字段齐全()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITSTK");
        await SeedProductAsync(name, salePrice: 12m, costPrice: 8m, stock: 30m, safetyStock: 5m);

        var body = await ReadBody(await Client.GetAsync($"/api/v1/stocks?keyword={Esc(name)}"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.True(data.TryGetProperty("list", out var list));
        Assert.True(data.TryGetProperty("total", out var total));
        Assert.True(data.TryGetProperty("page", out _));
        Assert.True(data.TryGetProperty("pageSize", out _));

        Assert.Equal(1, total.GetInt32());
        var row = list.EnumerateArray().Single();
        Assert.Equal(name, row.GetProperty("name").GetString());
        Assert.Equal(30m, row.GetProperty("stockQuantity").GetDecimal());
        Assert.Equal(5m, row.GetProperty("safetyStock").GetDecimal());
        Assert.Equal(240m, row.GetProperty("stockAmount").GetDecimal());   // 30 × 8
        Assert.Equal("正常", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 实时库存_按状态筛选时返回的每一行都符合该状态()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITSTK0");
        await SeedProductAsync(name, salePrice: 5m, costPrice: 3m, stock: 0m, safetyStock: 9m);

        var body = await ReadBody(await Client.GetAsync("/api/v1/stocks?status=" + Esc("缺货")));
        var list = body.GetProperty("data").GetProperty("list").EnumerateArray().ToList();

        Assert.Contains(list, x => x.GetProperty("name").GetString() == name);
        // 状态是派生值，筛选必须与派生结果一致：缺货 = 库存 <= 0
        Assert.All(list, x => Assert.True(x.GetProperty("stockQuantity").GetDecimal() <= 0m));
        Assert.All(list, x => Assert.Equal("缺货", x.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task 实时库存_分页参数生效()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await Client.GetAsync("/api/v1/stocks?page=1&pageSize=3"));
        var data = body.GetProperty("data");

        Assert.Equal(3, data.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, data.GetProperty("page").GetInt32());
        Assert.True(data.GetProperty("total").GetInt32() > 3, "种子数据商品数应多于 3");
        Assert.Equal(3, data.GetProperty("list").GetArrayLength());
    }

    // ================= 临期（四档分桶） =================

    [Fact]
    public async Task 临期列表_按剩余天数分到7天内30天内与已过期三档()
    {
        await LoginAsAdminAsync();
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));

        // 保质期 60 天：生产日期决定还剩几天到期
        var soon = NewTag("ITEXP7");    // 还剩 2 天 → 7 天内 + 30 天内
        var soonId = await SeedProductAsync(soon, salePrice: 6m, costPrice: 3m, hasExpiry: true, shelfLifeDays: 60);
        var expired = NewTag("ITEXPX"); // 已过期 10 天 → 已过期
        var expiredId = await SeedProductAsync(expired, salePrice: 6m, costPrice: 3m, hasExpiry: true, shelfLifeDays: 60);

        await SeedPurchaseWithProduceAsync(supplierId, DateTime.Today.AddDays(-58), (soonId, 10m, 3m));
        await SeedPurchaseWithProduceAsync(supplierId, DateTime.Today.AddDays(-70), (expiredId, 10m, 3m));

        var tag7 = await ExpiryRowsAsync("7");
        Assert.Contains(tag7, x => x.GetProperty("productId").GetInt32() == soonId);
        Assert.DoesNotContain(tag7, x => x.GetProperty("productId").GetInt32() == expiredId);
        var soonRow = tag7.First(x => x.GetProperty("productId").GetInt32() == soonId);
        Assert.Equal(2, soonRow.GetProperty("daysLeft").GetInt32());
        Assert.Equal("临期(7天内)", soonRow.GetProperty("status").GetString());

        var tag30 = await ExpiryRowsAsync("30");
        Assert.Contains(tag30, x => x.GetProperty("productId").GetInt32() == soonId);
        Assert.DoesNotContain(tag30, x => x.GetProperty("productId").GetInt32() == expiredId);

        var tagExpired = await ExpiryRowsAsync("expired");
        Assert.Contains(tagExpired, x => x.GetProperty("productId").GetInt32() == expiredId);
        Assert.DoesNotContain(tagExpired, x => x.GetProperty("productId").GetInt32() == soonId);
        var expiredRow = tagExpired.First(x => x.GetProperty("productId").GetInt32() == expiredId);
        Assert.Equal(-10, expiredRow.GetProperty("daysLeft").GetInt32());
        Assert.Equal("已过期", expiredRow.GetProperty("status").GetString());

        // 未处理页不显示已处理批次（这里都还没处理）
        Assert.All(tagExpired, x => Assert.False(x.GetProperty("isProcessed").GetBoolean()));
    }

    private async Task<List<JsonElement>> ExpiryRowsAsync(string tag)
    {
        var body = await ReadBody(await Client.GetAsync($"/api/v1/stocks/expiry?tag={tag}&pageSize=200"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        return body.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
    }

    // ================= 库存预警 =================

    [Fact]
    public async Task 库存预警_列出低于安全库存的商品且缺口等于安全库存减现有()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITWARN");
        await SeedProductAsync(name, salePrice: 5m, costPrice: 3m, stock: 2m, safetyStock: 10m);

        var body = await ReadBody(await Client.GetAsync("/api/v1/stocks/warnings"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var rows = body.GetProperty("data").EnumerateArray().ToList();

        var mine = rows.First(x => x.GetProperty("name").GetString() == name);
        Assert.Equal(2m, mine.GetProperty("stockQuantity").GetDecimal());
        Assert.Equal(10m, mine.GetProperty("safetyStock").GetDecimal());
        Assert.Equal(8m, mine.GetProperty("shortage").GetDecimal());     // 10 − 2
        Assert.Equal("预警", mine.GetProperty("status").GetString());

        // 预警表的判据是「库存 <= 安全库存」，逐行复核一遍
        Assert.All(rows, x => Assert.True(
            x.GetProperty("stockQuantity").GetDecimal() <= x.GetProperty("safetyStock").GetDecimal()));
    }

    // ================= 库存流水 =================

    [Fact]
    public async Task 库存流水_记下进货入库且可按类型筛选()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITLOG");
        var productId = await SeedProductAsync(name, salePrice: 5m, costPrice: 2m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        var orderNo = (await SeedPurchaseAsync(supplierId, (productId, 7m, 2m))).GetProperty("orderNo").GetString()!;

        var body = await ReadBody(await Client.GetAsync($"/api/v1/stock-logs?keyword={orderNo}"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var rows = body.GetProperty("data").GetProperty("list").EnumerateArray().ToList();

        var row = rows.First(x => x.GetProperty("refNo").GetString() == orderNo);
        Assert.Equal(name, row.GetProperty("productName").GetString());
        Assert.Equal("采购入库", row.GetProperty("changeType").GetString());
        Assert.Equal(7m, row.GetProperty("changeQty").GetDecimal());
        Assert.Equal(0m, row.GetProperty("beforeQty").GetDecimal());
        Assert.Equal(7m, row.GetProperty("afterQty").GetDecimal());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("createdByName").GetString()));

        // 类型筛选：换成别的类型就不该再回来
        var filtered = await ReadBody(await Client.GetAsync(
            $"/api/v1/stock-logs?keyword={orderNo}&changeType={Esc("销售出库")}"));
        Assert.Empty(filtered.GetProperty("data").GetProperty("list").EnumerateArray());
    }
}

/// <summary>
/// 「标记临期已处理」的集成测试 —— **单独一个类**。
///
/// 它是有副作用的（扣减批次库存 + 写「临期报损」流水），放在
/// <see cref="StockQueryIntegrationTests"/> 里会把同类的只读用例一起带坏
/// （同一类共享一个工厂、也就是共享一个 InMemory 库）。这个教训在
/// <c>DemoDataClearIntegrationTests</c> 上已经吃过一次。
/// </summary>
public class ExpiryMarkProcessedIntegrationTests : IntegrationSeedBase
{
    public ExpiryMarkProcessedIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task 标记临期已处理_扣减批次库存并写临期报损流水()
    {
        await LoginAsAdminAsync();
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        var name = NewTag("ITMEXP");
        var productId = await SeedProductAsync(name, salePrice: 6m, costPrice: 3m, hasExpiry: true, shelfLifeDays: 60);
        await SeedPurchaseWithProduceAsync(supplierId, DateTime.Today.AddDays(-70), (productId, 6m, 3m));   // 已过期

        var rows = (await ReadBody(await Client.GetAsync("/api/v1/stocks/expiry?tag=expired&pageSize=200")))
            .GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        var batch = rows.First(x => x.GetProperty("productId").GetInt32() == productId);
        var batchId = batch.GetProperty("id").GetInt64();
        Assert.Equal(6m, batch.GetProperty("quantity").GetDecimal());
        Assert.Equal(6m, await StockOfAsync(name));

        var resp = await PostJsonAsync("/api/v1/stocks/expiry/mark-processed", new[] { batchId });
        Assert.Equal(0, (await ReadBody(resp)).GetProperty("code").GetInt32());

        // 报损把批次那 6 件从库存里扣掉
        Assert.Equal(0m, await StockOfAsync(name));

        var processed = (await ReadBody(await Client.GetAsync("/api/v1/stocks/expiry?tag=processed&pageSize=200")))
            .GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        var done = processed.First(x => x.GetProperty("id").GetInt64() == batchId);
        Assert.True(done.GetProperty("isProcessed").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(done.GetProperty("processedAt").GetString()));

        var logs = (await ReadBody(await Client.GetAsync(
            $"/api/v1/stock-logs?keyword={Uri.EscapeDataString(name)}&changeType={Uri.EscapeDataString("临期报损")}")))
            .GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.NotEmpty(logs);
        Assert.Equal(-6m, logs[0].GetProperty("changeQty").GetDecimal());
    }

    [Fact]
    public async Task 标记临期已处理_空数组与不存在批次都返回业务失败()
    {
        await LoginAsAdminAsync();

        var empty = await ReadBody(await PostJsonAsync("/api/v1/stocks/expiry/mark-processed", Array.Empty<long>()));
        Assert.NotEqual(0, empty.GetProperty("code").GetInt32());

        var missing = await ReadBody(await PostJsonAsync("/api/v1/stocks/expiry/mark-processed", new[] { 999999L }));
        Assert.NotEqual(0, missing.GetProperty("code").GetInt32());
        Assert.Contains("不存在", missing.GetProperty("message").GetString());
    }
}
