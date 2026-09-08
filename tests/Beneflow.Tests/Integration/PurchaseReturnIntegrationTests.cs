using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 采购退货集成测试：验证「创建采购退货单 → 扣减库存 → 计算应退金额 → 列表可见」整条链路，
/// 以及库存不足拦截。端到端经由真实 HTTP 管线。
/// </summary>
public class PurchaseReturnIntegrationTests : IntegrationTestBase
{
    public PurchaseReturnIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static decimal StockOf(JsonElement listBody, int productId)
    {
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .Where(x => x.GetProperty("id").GetInt32() == productId).ToList();
        return items.Count == 0 ? -1 : items[0].GetProperty("stockQuantity").GetDecimal();
    }

    private async Task<int> SeedProductAsync(string name, decimal costPrice, decimal stock)
    {
        var cats = await ReadBody(await Client.GetAsync("/api/v1/categories"));
        var catId = cats.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
        var resp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "", name, categoryId = catId, unit = "瓶", salePrice = costPrice + 5,
            costPrice, stockQuantity = stock, safetyStock = 0, hasExpiry = false,
            shelfLifeDays = 0, isWeighted = false, status = true,
        });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    private async Task<int> CreateSupplierAsync(string name)
    {
        var resp = await PostJsonAsync("/api/v1/suppliers", new { name, status = true });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task PurchaseReturn_ReducesStock_AndComputesRefund()
    {
        await LoginAsAdminAsync();
        var name = "ITPR_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 5m, 10);
        var supplierId = await CreateSupplierAsync("ITSUP_" + Guid.NewGuid().ToString("N")[..8]);

        var retResp = await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 3, costPrice = 5.00m } },
            returnTotal = 15.00m,
            reason = "质量问题",
        });
        var retData = await ExpectOk(retResp);
        Assert.True(retData.GetProperty("id").GetInt32() > 0);
        Assert.Equal(15.00m, retData.GetProperty("refundTotal").GetDecimal());

        // 库存应扣减到 7（10 - 3）
        var listBody = await ReadBody(await Client.GetAsync($"/api/v1/products?keyword={name}"));
        Assert.Equal(7, StockOf(listBody, productId));
    }

    [Fact]
    public async Task PurchaseReturn_AppearsInList()
    {
        await LoginAsAdminAsync();
        var name = "ITPR2_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 4m, 10);
        var supplierId = await CreateSupplierAsync("ITSUP_" + Guid.NewGuid().ToString("N")[..8]);

        var retResp = await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 2, costPrice = 4.00m } },
            returnTotal = 8.00m,
        });
        var retData = await ExpectOk(retResp);
        var orderNo = retData.GetProperty("orderNo").GetString()!;

        var listBody = await ReadBody(await Client.GetAsync($"/api/v1/purchase-returns?keyword={orderNo}"));
        var ids = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .Select(x => x.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(retData.GetProperty("id").GetInt32(), ids);
    }

    [Fact]
    public async Task PurchaseReturn_InsufficientStock_ReturnsFail()
    {
        await LoginAsAdminAsync();
        var name = "ITPR3_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 5m, 2); // 仅 2 件
        var supplierId = await CreateSupplierAsync("ITSUP_" + Guid.NewGuid().ToString("N")[..8]);

        var retResp = await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 5, costPrice = 5.00m } },
            returnTotal = 25.00m,
        });
        var body = await ReadBody(retResp);
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("库存不足", body.GetProperty("message").GetString());
    }
}
