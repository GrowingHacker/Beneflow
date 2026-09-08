using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 库存盘点集成测试：验证「创建盘点单（草稿/已确认）→ 确认盘点 → 按实盘数调整库存 + 写盘点流水」整条链路。
/// </summary>
public class StockCheckIntegrationTests : IntegrationTestBase
{
    public StockCheckIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static decimal StockOf(JsonElement listBody, int productId)
    {
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .Where(x => x.GetProperty("id").GetInt32() == productId).ToList();
        return items.Count == 0 ? -1 : items[0].GetProperty("stockQuantity").GetDecimal();
    }

    private async Task<int> SeedProductAsync(string name, decimal stock)
    {
        var cats = await ReadBody(await Client.GetAsync("/api/v1/categories"));
        var catId = cats.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
        var resp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "", name, categoryId = catId, unit = "瓶", salePrice = 12, costPrice = 5,
            stockQuantity = stock, safetyStock = 0, hasExpiry = false, shelfLifeDays = 0,
            isWeighted = false, status = true,
        });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task StockCheck_Draft_KeepsStock_Confirm_AdjustsToActual()
    {
        await LoginAsAdminAsync();
        var name = "ITSC_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 10);

        // 草稿盘点：账面 10、实盘 7
        var createResp = await PostJsonAsync("/api/v1/stocks/check", new
        {
            range = "全部商品",
            status = "草稿",
            items = new[] { new { id = productId, bookQty = 10m, actualQty = 7m } },
        });
        var createData = await ExpectOk(createResp);
        var checkId = createData.GetProperty("id").GetInt32();
        Assert.True(checkId > 0);

        // 草稿不调整库存
        var afterDraft = await ReadBody(await Client.GetAsync($"/api/v1/products?keyword={name}"));
        Assert.Equal(10, StockOf(afterDraft, productId));

        // 确认盘点：库存应调整为实盘数 7
        var confirmResp = await Client.PostAsync($"/api/v1/stocks/check/{checkId}/confirm", null!);
        await ExpectOk(confirmResp);

        var afterConfirm = await ReadBody(await Client.GetAsync($"/api/v1/products?keyword={name}"));
        Assert.Equal(7, StockOf(afterConfirm, productId));
    }

    [Fact]
    public async Task StockCheck_CreateConfirmed_AdjustsImmediately()
    {
        await LoginAsAdminAsync();
        var name = "ITSC2_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 10);

        // 直接以「已确认」创建：库存立即调整为实盘数 5
        var createResp = await PostJsonAsync("/api/v1/stocks/check", new
        {
            range = "全部商品",
            status = "已确认",
            items = new[] { new { id = productId, bookQty = 10m, actualQty = 5m } },
        });
        var createData = await ExpectOk(createResp);
        Assert.True(createData.GetProperty("id").GetInt32() > 0);

        var afterCreate = await ReadBody(await Client.GetAsync($"/api/v1/products?keyword={name}"));
        Assert.Equal(5, StockOf(afterCreate, productId));
    }

    [Fact]
    public async Task StockCheck_AppearsInList()
    {
        await LoginAsAdminAsync();
        var name = "ITSC3_" + Guid.NewGuid().ToString("N")[..8];
        var productId = await SeedProductAsync(name, 10);

        var createResp = await PostJsonAsync("/api/v1/stocks/check", new
        {
            range = "全部商品",
            status = "草稿",
            items = new[] { new { id = productId, bookQty = 10m, actualQty = 9m } },
        });
        var createData = await ExpectOk(createResp);
        var orderNo = createData.GetProperty("orderNo").GetString()!;

        var listBody = await ReadBody(await Client.GetAsync("/api/v1/stocks/check"));
        var orderNos = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .Select(x => x.GetProperty("orderNo").GetString()).ToList();
        Assert.Contains(orderNo, orderNos);
    }
}
