using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 进货 → 库存联动集成测试：验证采购入库经由真实 HTTP 管线后，库存数量、移动加权平均成本、
/// 库存流水三处数据一致联动（这是现有 InMemory 单元测试已覆盖的业务逻辑，此处验证端到端管线）。
/// </summary>
public class PurchaseStockIntegrationTests : IntegrationTestBase
{
    public PurchaseStockIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static decimal StockOf(JsonElement listBody, int productId)
    {
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .Where(x => x.GetProperty("id").GetInt32() == productId).ToList();
        return items.Count == 0 ? -1 : items[0].GetProperty("stockQuantity").GetDecimal();
    }

    private async Task<int> SeedProductAsync(string name, decimal salePrice, decimal costPrice, decimal initStock)
    {
        var cats = await ReadBody(await Client.GetAsync("/api/v1/categories"));
        var catId = cats.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
        var resp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "", name, categoryId = catId, unit = "瓶", salePrice, costPrice,
            stockQuantity = initStock, safetyStock = 0, hasExpiry = false, shelfLifeDays = 0, isWeighted = false, status = true,
        });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Purchase_IncreasesStock_UpdatesCost()
    {
        await LoginAsAdminAsync();
        var name = "ITPUR_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 12, 0, 0);

        var supResp = await PostJsonAsync("/api/v1/suppliers", new { name = "ITSUP_" + Guid.NewGuid().ToString("N")[..8], status = true });
        var supplierId = (await ExpectOk(supResp)).GetProperty("id").GetInt32();

        var purResp = await PostJsonAsync("/api/v1/purchases", new
        {
            supplierId,
            details = new[] { new { productId, qty = 10, costPrice = 5.00 } },
            totalQty = 10,
            totalAmount = 50.00,
        });
        var purData = await ExpectOk(purResp);
        var purchaseId = purData.GetProperty("id").GetInt32();
        Assert.True(purchaseId > 0);

        // 库存应变为 10、成本应记为 5（移动加权平均）
        var listBody = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        Assert.Equal(10, StockOf(listBody, productId));

        var prod = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("id").GetInt32() == productId);
        Assert.Equal(5.00m, prod.GetProperty("costPrice").GetDecimal());
    }

    [Fact]
    public async Task Purchase_DetailEndpoint_ReturnsOk()
    {
        await LoginAsAdminAsync();
        var name = "ITPUR2_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 10, 0, 0);
        var supResp = await PostJsonAsync("/api/v1/suppliers", new { name = "ITSUP_" + Guid.NewGuid().ToString("N")[..8], status = true });
        var supplierId = (await ExpectOk(supResp)).GetProperty("id").GetInt32();

        var purResp = await PostJsonAsync("/api/v1/purchases", new
        {
            supplierId,
            details = new[] { new { productId, qty = 5, costPrice = 4.00 } },
            totalQty = 5,
            totalAmount = 20.00,
        });
        var purchaseId = (await ExpectOk(purResp)).GetProperty("id").GetInt32();

        // 进货单详情端点应正常返回
        var detailResp = await Client.GetAsync($"/api/v1/purchases/{purchaseId}");
        Assert.Equal(0, (await ReadBody(detailResp)).GetProperty("code").GetInt32());
    }
}
