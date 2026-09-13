using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 库存并发一致性集成测试。
///
/// 场景：同一个商品只剩若干件库存，并发提交多个各买 1 件的收银请求（走真实 HTTP 管线）。
/// 守住的核心不变量是「成功单数 == 库存件数」且「库存不为负」——也就是不超卖。
///
/// <para>
/// <b>关于本测试的验证强度，有一点必须说清楚</b>：
/// 在 EF Core InMemory 下，<c>OrderNo</c> 上的唯一索引会充当一道「意外的防线」——
/// 当多个并发请求恰好算出同一个单号时，只有一个能插入成功，其余在扣减库存之前就抛异常退出。
/// 因此即使把库存锁摘掉，本测试也可能侥幸通过。<b>它能守住不变量，但不足以单独证明库存锁生效。</b>
/// </para>
///
/// <para>
/// 锁语义本身由 <c>StockMutationLockTests</c> 用确定性的同步原语验证（不依赖线程调度），
/// 两者互补：那边证明「锁确实在串行」，这边证明「整条链路的结果不变量成立」。
/// </para>
///
/// <para>
/// 另外，本测试跑在 InMemory 上，而 InMemory 不支持真正的事务，因此它无法覆盖
/// 「回滚是否彻底」。若将来改为多实例部署，需要换成数据库层的并发方案
/// （<c>Product.RowVersion</c> 乐观并发，或扣减改为带 <c>WHERE StockQuantity &gt;= qty</c>
/// 的条件更新并校验受影响行数），届时本测试应改为针对真实 SQL Server 运行。
/// </para>
/// </summary>
public class StockConcurrencyIntegrationTests : IntegrationTestBase
{
    public StockConcurrencyIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static decimal StockOf(JsonElement listBody, int productId)
    {
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .Where(x => x.GetProperty("id").GetInt32() == productId).ToList();
        return items.Count == 0 ? -1 : items[0].GetProperty("stockQuantity").GetDecimal();
    }

    private async Task<int> SeedProductAsync(string name, decimal salePrice, decimal stock)
    {
        var cats = await ReadBody(await Client.GetAsync("/api/v1/categories"));
        var catId = cats.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
        var resp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "", name, categoryId = catId, unit = "瓶", salePrice, costPrice = 0,
            stockQuantity = stock, safetyStock = 0, hasExpiry = false, shelfLifeDays = 0,
            isWeighted = false, status = true,
        });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    private async Task<int> SellOneAsync(int productId)
    {
        var resp = await PostJsonAsync("/api/v1/sales", new
        {
            items = new[] { new { productId, qty = 1m, unitPrice = 12.00m, subTotal = 12.00m } },
            totalAmount = 12.00m,
            discountAmount = 0m,
            payAmount = 12.00m,
            payMethod = "现金",
            cashAmount = 12.00m,
            changeAmount = 0m,
            isCredit = false,
        });
        var body = await ReadBody(resp);
        return body.GetProperty("code").GetInt32();
    }

    [Fact]
    public async Task 并发收银最后一件库存_只允许一单成功且库存不为负()
    {
        await LoginAsAdminAsync();
        var name = "ITCONC_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 12, stock: 1);

        const int concurrency = 8;

        // 同时发起 N 个收银请求，每个都要买走这仅剩的 1 件
        var codes = await Task.WhenAll(
            Enumerable.Range(0, concurrency).Select(_ => SellOneAsync(productId)));

        // 恰好 1 单成功——超卖会让成功的数量大于 1
        Assert.Equal(1, codes.Count(c => c == 0));

        // 库存必须归零，且绝不为负
        var list = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        Assert.Equal(0, StockOf(list, productId));
    }

    [Fact]
    public async Task 并发收银充足库存_成功数等于库存件数且不超卖()
    {
        await LoginAsAdminAsync();
        var name = "ITCONC2_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 12, stock: 5);

        // 12 个并发请求抢 5 件库存：应当恰好 5 单成功、7 单失败
        var codes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => SellOneAsync(productId)));

        Assert.Equal(5, codes.Count(c => c == 0));

        var list = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        Assert.Equal(0, StockOf(list, productId));
    }
}
