using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 三类「作废」端点的上线前集成测试：销售单作废、进货单作废、采购退货作废。
///
/// 之前这一层是空的 —— 三类作废都只测过「建单」，没测过「作废」。
/// 而作废恰恰是三处**有副作用**的操作（回退库存 / 回补库存 / 从对账剔除），
/// 且方向都是「把已经写进去的数据撤回来」，写错方向会静默把库存算错，
/// 页面上看不出任何异常。所以每条用例都断言「库存数字变了多少」，
/// 而不只是断言接口返回 200。
///
/// 口径（见项目笔记「采购退货作废 + 供应商对账口径」）：
/// 作废 ≠ 删除。退货单是「已退款给供应商」的凭证，所以只回补库存 + 标记，
/// 并一律从供应商对账里剔除。
/// </summary>
public class VoidIntegrationTests : IntegrationSeedBase
{
    public VoidIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private async Task<(int Code, string? Message, JsonElement Data)> CallVoidAsync(string path)
    {
        var resp = await PostAsync(path);
        return Unwrap(await ReadBody(resp));
    }

    // ================= 鉴权 =================

    [Fact]
    public async Task 三类作废端点未登录都返回401()
    {
        foreach (var path in new[]
                 {
                     "/api/v1/sales/1/void",
                     "/api/v1/purchases/1/void",
                     "/api/v1/purchase-returns/1/void",
                 })
        {
            var resp = await Client.PostAsync(path, null);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    // ================= 销售单作废 =================

    [Fact]
    public async Task 销售单作废_回补库存并写一条作废回补流水()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITVOIDS");
        var productId = await SeedProductAsync(name, salePrice: 10m, costPrice: 4m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 10m, 4m));

        var sale = await SeedSaleAsync((productId, 3m, 10m));
        var saleId = sale.GetProperty("id").GetInt32();
        var orderNo = sale.GetProperty("orderNo").GetString()!;
        Assert.Equal(7m, await StockOfAsync(name));      // 10 − 3

        var (code, _, _) = await CallVoidAsync($"/api/v1/sales/{saleId}/void");
        Assert.Equal(0, code);

        // 作废要把货**收回货架**：库存回到 10。
        // 方向记牢：销售单作废 = 库存往上走（把卖出去的拿回来，流水「作废回补」），
        // 进货单作废 = 库存往下走（退货给供应商，流水「作废回退」）—— 两个方向相反，
        // 写反了页面上一点异常都看不出来，只有库存数字会悄悄错掉。
        Assert.Equal(10m, await StockOfAsync(name));

        var logs = await ReadBody(await Client.GetAsync(
            $"/api/v1/stock-logs?keyword={orderNo}&changeType={Uri.EscapeDataString("作废回补")}"));
        var rows = logs.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.Equal(3m, rows[0].GetProperty("changeQty").GetDecimal());

        // 列表里仍能查到，但状态是已作废（作废 ≠ 删除）
        var listBody = await ReadBody(await Client.GetAsync($"/api/v1/sales?keyword={orderNo}"));
        var row = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("orderNo").GetString() == orderNo);
        Assert.Equal("已作废", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task 销售单作废_重复作废与不存在的单号都返回业务失败()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITVOIDS2");
        var productId = await SeedProductAsync(name, salePrice: 10m, costPrice: 4m, stock: 5m);
        var saleId = (await SeedSaleAsync((productId, 1m, 10m))).GetProperty("id").GetInt32();

        Assert.Equal(0, (await CallVoidAsync($"/api/v1/sales/{saleId}/void")).Code);

        var again = await CallVoidAsync($"/api/v1/sales/{saleId}/void");
        Assert.NotEqual(0, again.Code);
        Assert.Contains("已作废", again.Message);

        var missing = await CallVoidAsync("/api/v1/sales/999999/void");
        Assert.NotEqual(0, missing.Code);
        Assert.Contains("不存在", missing.Message);
    }

    // ================= 进货单作废 =================

    [Fact]
    public async Task 进货单作废_回退库存并写一条作废回退流水()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITVOIDP");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 5m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));

        var purchase = await SeedPurchaseAsync(supplierId, (productId, 10m, 5m));
        var purchaseId = purchase.GetProperty("id").GetInt32();
        var orderNo = purchase.GetProperty("orderNo").GetString()!;
        Assert.Equal(10m, await StockOfAsync(name));

        Assert.Equal(0, (await CallVoidAsync($"/api/v1/purchases/{purchaseId}/void")).Code);

        // 进货作废要把货退回去 ⇒ 库存回退到 0
        Assert.Equal(0m, await StockOfAsync(name));

        var logs = await ReadBody(await Client.GetAsync(
            $"/api/v1/stock-logs?keyword={orderNo}&changeType={Uri.EscapeDataString("作废回退")}"));
        var rows = logs.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.Equal(-10m, rows[0].GetProperty("changeQty").GetDecimal());
        Assert.Equal(10m, rows[0].GetProperty("beforeQty").GetDecimal());
        Assert.Equal(0m, rows[0].GetProperty("afterQty").GetDecimal());

        var listBody = await ReadBody(await Client.GetAsync($"/api/v1/purchases?keyword={orderNo}"));
        var row = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("orderNo").GetString() == orderNo);
        Assert.True(row.GetProperty("isVoided").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("voidedAt").ValueKind);
    }

    [Fact]
    public async Task 进货单作废_重复作废返回业务失败()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITVOIDP2");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 5m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        var purchaseId = (await SeedPurchaseAsync(supplierId, (productId, 3m, 5m))).GetProperty("id").GetInt32();
        Assert.Equal(3m, await StockOfAsync(name));

        Assert.Equal(0, (await CallVoidAsync($"/api/v1/purchases/{purchaseId}/void")).Code);
        Assert.Equal(0m, await StockOfAsync(name));

        var again = await CallVoidAsync($"/api/v1/purchases/{purchaseId}/void");
        Assert.NotEqual(0, again.Code);

        // 重复作废绝不能把库存再加回来（这正是这类用例要守的东西）
        Assert.Equal(0m, await StockOfAsync(name));
    }

    // ================= 采购退货作废 =================

    [Fact]
    public async Task 采购退货作废_回补库存且列表标记已作废()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITVOIDR");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 5m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 10m, 5m));

        var returnResp = await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 4m, costPrice = 5m } },
            returnTotal = 20m,
            reason = "作废用例",
        });
        var returnData = await ExpectOk(returnResp);
        var returnId = returnData.GetProperty("id").GetInt32();
        var returnOrderNo = returnData.GetProperty("orderNo").GetString()!;

        Assert.Equal(6m, await StockOfAsync(name));      // 退货出库后 10 − 4

        Assert.Equal(0, (await CallVoidAsync($"/api/v1/purchase-returns/{returnId}/void")).Code);

        // 作废回补：10 − 4 + 4 = 10
        Assert.Equal(10m, await StockOfAsync(name));

        var logs = await ReadBody(await Client.GetAsync(
            $"/api/v1/stock-logs?keyword={returnOrderNo}&changeType={Uri.EscapeDataString("采购退货作废回补")}"));
        var rows = logs.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.Equal(4m, rows[0].GetProperty("changeQty").GetDecimal());

        var listBody = await ReadBody(await Client.GetAsync($"/api/v1/purchase-returns?keyword={returnOrderNo}"));
        var row = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("orderNo").GetString() == returnOrderNo);
        Assert.Equal("已作废", row.GetProperty("status").GetString());
        Assert.True(row.GetProperty("isVoided").GetBoolean());
    }

    [Fact]
    public async Task 采购退货作废_重复作废返回业务失败且不重复回补库存()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITVOIDR2");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 5m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        await SeedPurchaseAsync(supplierId, (productId, 10m, 5m));

        var returnId = (await ExpectOk(await PostJsonAsync("/api/v1/purchase-returns", new
        {
            supplierId,
            items = new[] { new { productId, qty = 2m, costPrice = 5m } },
            returnTotal = 10m,
        }))).GetProperty("id").GetInt32();

        Assert.Equal(0, (await CallVoidAsync($"/api/v1/purchase-returns/{returnId}/void")).Code);
        Assert.Equal(10m, await StockOfAsync(name));

        var again = await CallVoidAsync($"/api/v1/purchase-returns/{returnId}/void");
        Assert.NotEqual(0, again.Code);
        Assert.Contains("已作废", again.Message);

        // 关键：第二次作废不能把库存再抬到 12
        Assert.Equal(10m, await StockOfAsync(name));
    }

    [Fact]
    public async Task 采购退货作废_不存在的单号返回业务失败()
    {
        await LoginAsAdminAsync();
        var missing = await CallVoidAsync("/api/v1/purchase-returns/999999/void");
        Assert.NotEqual(0, missing.Code);
        Assert.Contains("不存在", missing.Message);
    }
}
