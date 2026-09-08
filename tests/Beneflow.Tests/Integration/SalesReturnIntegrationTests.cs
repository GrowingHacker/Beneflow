using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 销售 → 退货跨模块集成测试：验证「进货入库 → 销售扣库存 → 销售退货回补库存」整条业务链在
/// 真实 HTTP 管线下的数据一致性（库存数量在三个环节正确联动）。
/// </summary>
public class SalesReturnIntegrationTests : IntegrationTestBase
{
    public SalesReturnIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static decimal StockOf(JsonElement listBody, int productId)
    {
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .Where(x => x.GetProperty("id").GetInt32() == productId).ToList();
        return items.Count == 0 ? -1 : items[0].GetProperty("stockQuantity").GetDecimal();
    }

    private async Task<int> SeedProductAsync(string name, decimal salePrice, decimal costPrice, decimal stock)
    {
        var cats = await ReadBody(await Client.GetAsync("/api/v1/categories"));
        var catId = cats.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
        var resp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "", name, categoryId = catId, unit = "瓶", salePrice, costPrice,
            stockQuantity = stock, safetyStock = 0, hasExpiry = false, shelfLifeDays = 0, isWeighted = false, status = true,
        });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    private async Task<int> CreateSupplierAsync(string name)
    {
        var resp = await PostJsonAsync("/api/v1/suppliers", new { name, status = true });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Sale_DecreasesStock_AndReturn_Replenishes()
    {
        await LoginAsAdminAsync();
        var name = "ITSALE_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 12, 0, 0);
        var supplierId = await CreateSupplierAsync("ITSUP_" + Guid.NewGuid().ToString("N")[..8]);

        // 1) 进货 10 件，成本 5
        await ExpectOk(await PostJsonAsync("/api/v1/purchases", new
        {
            supplierId,
            details = new[] { new { productId, qty = 10, costPrice = 5.00 } },
            totalQty = 10,
            totalAmount = 50.00,
        }));

        // 2) 销售 3 件（售价以数据库 12 为准）
        var saleResp = await PostJsonAsync("/api/v1/sales", new
        {
            items = new[] { new { productId, qty = 3, unitPrice = 12.00, subTotal = 36.00 } },
            totalAmount = 36.00,
            discountAmount = 0,
            payAmount = 36.00,
            payMethod = "现金",
            cashAmount = 50.00,
            changeAmount = 0,
            isCredit = false,
        });
        var saleData = await ExpectOk(saleResp);
        var orderNo = saleData.GetProperty("orderNo").GetString()!;

        // 库存应扣到 7
        var afterSale = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        Assert.Equal(7, StockOf(afterSale, productId));

        // 3) 退货 2 件
        var retResp = await PostJsonAsync("/api/v1/sale-returns", new
        {
            originalOrderNo = orderNo,
            items = new[] { new { name, qty = 2, unitPrice = 12.00 } },
            refundMethod = "原路退回",
            refundTotal = 24.00,
        });
        var retData = await ExpectOk(retResp);
        Assert.Equal(24.00m, retData.GetProperty("refundTotal").GetDecimal());

        // 库存应回补到 9
        var afterReturn = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        Assert.Equal(9, StockOf(afterReturn, productId));
    }

    [Fact]
    public async Task Sale_InsufficientStock_ReturnsFail()
    {
        await LoginAsAdminAsync();
        var name = "ITSALE2_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 12, 0, 2); // 仅 2 件库存
        await CreateSupplierAsync("ITSUP_" + Guid.NewGuid().ToString("N")[..8]);

        // 尝试销售 5 件应失败
        var saleResp = await PostJsonAsync("/api/v1/sales", new
        {
            items = new[] { new { productId, qty = 5, unitPrice = 12.00, subTotal = 60.00 } },
            totalAmount = 60.00,
            discountAmount = 0,
            payAmount = 60.00,
            payMethod = "现金",
            cashAmount = 100.00,
            changeAmount = 0,
            isCredit = false,
        });
        var body = await ReadBody(saleResp);
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("库存不足", body.GetProperty("message").GetString());
    }
}
