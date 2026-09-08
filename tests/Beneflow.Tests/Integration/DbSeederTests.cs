using Beneflow.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 种子数据回放一致性测试：直接在独立 InMemory 库上跑 DbSeeder.SeedAsync，
/// 断言回放不抛异常，且回放后每个商品的库存流水累加值恰好等于其 StockQuantity（账实一致）。
/// 这守护了「散装花生米 回放=0 目标=15」这类因期初建账漏写导致回放不一致的问题。
/// </summary>
public class DbSeederTests
{
    [Fact]
    public async Task SeedAsync_ReplayReconcilesAllProducts()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"Seed_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new AppDbContext(options);

        // 回放路径若不一致会在此处抛 InvalidOperationException
        var ex = await Record.ExceptionAsync(() => DbSeeder.SeedAsync(db));
        Assert.Null(ex);

        var products = await db.Products.ToListAsync();
        Assert.NotEmpty(products);
        foreach (var p in products)
        {
            var computed = await db.StockLogs
                .Where(l => l.ProductId == p.Id)
                .SumAsync(l => l.ChangeQty);
            Assert.Equal(p.StockQuantity, computed);
        }
    }
}
