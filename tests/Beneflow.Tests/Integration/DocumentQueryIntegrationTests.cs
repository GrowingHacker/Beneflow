using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 单据查询类端点（销售单列表/详情/按单号、进货单列表/详情/编辑/批量、销售退货单列表）的上线前集成测试。
///
/// 这一组守两件在页面上很容易被忽略的事：
/// ① **作废单仍在列表里、但不进合计** —— 列表的 summary 口径必须与导出（<c>SummaryExcludeField=status</c>）
///    一致，两处一漂，页面与导出就对不上账；
/// ② **编辑进货单 = 就地回退原明细 + 按新明细入库** —— 库存必须净变化正确（改成 8 件就是 8，不是 5+8=13），
///    且单据号不变、<c>isVoided</c> 仍为 false（不是「作废旧单 + 新建新单」）。
/// </summary>
public class DocumentQueryIntegrationTests : IntegrationSeedBase
{
    public DocumentQueryIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private async Task<int> SaleIdOfAsync(string orderNo) =>
        (await ReadBody(await Client.GetAsync($"/api/v1/sales?keyword={orderNo}")))
        .GetProperty("data").GetProperty("list").EnumerateArray()
        .Single(x => x.GetProperty("orderNo").GetString() == orderNo)
        .GetProperty("id").GetInt32();

    // ================= 销售单 =================

    [Fact]
    public async Task 销售单列表_可筛选且详情与列表金额一致()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITDS");
        var productId = await SeedProductAsync(name, salePrice: 10m, costPrice: 6m, stock: 20m);
        var sale = await SeedSaleAsync((productId, 2m, 10m));
        var orderNo = sale.GetProperty("orderNo").GetString()!;

        var data = (await ReadBody(await Client.GetAsync($"/api/v1/sales?keyword={orderNo}"))).GetProperty("data");
        var row = data.GetProperty("list").EnumerateArray().Single();
        Assert.Equal(20m, row.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(20m, row.GetProperty("payAmount").GetDecimal());
        Assert.Equal("现金", row.GetProperty("payMethod").GetString());
        Assert.Equal("已完成", row.GetProperty("status").GetString());

        // 详情与列表必须同值：列表与详情弹窗并排看，差一分钱都是 bug
        var detail = (await ReadBody(await Client.GetAsync($"/api/v1/sales/{sale.GetProperty("id").GetInt32()}")))
            .GetProperty("data");
        Assert.Equal(row.GetProperty("payAmount").GetDecimal(), detail.GetProperty("payAmount").GetDecimal());
        var items = detail.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(name, items[0].GetProperty("name").GetString());
        Assert.Equal(2m, items[0].GetProperty("qty").GetDecimal());
    }

    [Fact]
    public async Task 销售单_按单号查询与小票补打场景一致()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITDSN"), salePrice: 6m, costPrice: 3m, stock: 10m);
        var orderNo = (await SeedSaleAsync((productId, 1m, 6m))).GetProperty("orderNo").GetString()!;

        var body = await ReadBody(await Client.GetAsync($"/api/v1/sales/by-no/{orderNo}"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        Assert.Equal(orderNo, body.GetProperty("data").GetProperty("orderNo").GetString());

        var missing = await ReadBody(await Client.GetAsync("/api/v1/sales/by-no/SO99999999001"));
        Assert.NotEqual(0, missing.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 销售单列表_作废单仍在列表里但不进合计()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITDSV"), salePrice: 10m, costPrice: 4m, stock: 20m);
        var orderNo = (await SeedSaleAsync((productId, 3m, 10m))).GetProperty("orderNo").GetString()!;
        await ExpectOk(await PostAsync($"/api/v1/sales/{await SaleIdOfAsync(orderNo)}/void"));

        var data = (await ReadBody(await Client.GetAsync($"/api/v1/sales?keyword={orderNo}"))).GetProperty("data");
        Assert.Equal("已作废", data.GetProperty("list").EnumerateArray().Single().GetProperty("status").GetString());

        // 合计口径：作废单不参与统计（否则合计会虚高）
        var summary = data.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("orders").GetInt32());
        Assert.Equal(0m, summary.GetProperty("payAmount").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("receivedAmount").GetDecimal());
    }

    // ================= 销售退货单列表 =================

    [Fact]
    public async Task 销售退货单列表_退货后可见()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITDSR");
        var productId = await SeedProductAsync(name, salePrice: 10m, costPrice: 6m, stock: 20m);
        var orderNo = (await SeedSaleAsync((productId, 4m, 10m))).GetProperty("orderNo").GetString()!;

        await ExpectOk(await PostJsonAsync("/api/v1/sale-returns", new
        {
            originalOrderNo = orderNo,
            items = new[] { new { name, qty = 1m, unitPrice = 10m } },
            refundMethod = "原路退回",
            refundTotal = 10m,
        }));

        var data = (await ReadBody(await Client.GetAsync("/api/v1/sale-returns?page=1&pageSize=20"))).GetProperty("data");
        var row = data.GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("saleOrderNo").GetString() == orderNo);
        // 这一行只有退款额与退款方式，没有「状态」字段（退货单不存在部分退的状态机）
        Assert.Equal(10m, row.GetProperty("refundAmount").GetDecimal());
        Assert.Equal("原路退回", row.GetProperty("refundMethod").GetString());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("createdByName").GetString()));
    }

    // ================= 进货单 =================

    [Fact]
    public async Task 进货单列表_含供应商与品种数()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITDP"), salePrice: 9m, costPrice: 4m);
        var supplierName = NewTag("ITSUP");
        var supplierId = await SeedSupplierAsync(supplierName);
        var orderNo = (await SeedPurchaseAsync(supplierId, (productId, 5m, 4m))).GetProperty("orderNo").GetString()!;

        var row = (await ReadBody(await Client.GetAsync($"/api/v1/purchases?keyword={orderNo}")))
            .GetProperty("data").GetProperty("list").EnumerateArray().Single();

        Assert.Equal(supplierName, row.GetProperty("supplierName").GetString());
        Assert.Equal(1, row.GetProperty("itemCount").GetInt32());
        Assert.Equal(5m, row.GetProperty("totalQty").GetDecimal());
        Assert.Equal(20m, row.GetProperty("totalAmount").GetDecimal());
        Assert.False(row.GetProperty("isVoided").GetBoolean());
    }

    [Fact]
    public async Task 进货单列表_可按供应商精确筛选()
    {
        await LoginAsAdminAsync();
        var productId = await SeedProductAsync(NewTag("ITDPF"), salePrice: 9m, costPrice: 4m);

        // 两家名字互为前缀的供应商：拿供应商名去撞 keyword 会把两家一起捞出来 ——
        // 这正是供应商列表的「进货记录」弹窗必须按 supplierId 筛、而不是传名称的原因。
        var mineName = NewTag("ITSUP");
        var mineId = await SeedSupplierAsync(mineName);
        var otherId = await SeedSupplierAsync(mineName[..^1]);

        await SeedPurchaseAsync(mineId, (productId, 2m, 4m));
        await SeedPurchaseAsync(otherId, (productId, 3m, 4m));

        // 对照组：按名称模糊查确实串单（弹窗若照这么做，标题写着 A 家的名字、表里混着 B 家的单）
        var byName = (await ReadBody(await Client.GetAsync($"/api/v1/purchases?keyword={mineName[..^1]}")))
            .GetProperty("data");
        Assert.Equal(2, byName.GetProperty("total").GetInt32());

        var data = (await ReadBody(await Client.GetAsync($"/api/v1/purchases?supplierId={mineId}")))
            .GetProperty("data");
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        var row = data.GetProperty("list").EnumerateArray().Single();
        Assert.Equal(mineId, row.GetProperty("supplierId").GetInt32());
        Assert.Equal(2m, row.GetProperty("totalQty").GetDecimal());
    }

    [Fact]
    public async Task 进货单详情_带供应商与明细行()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITDPD");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 4m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        var purchase = await SeedPurchaseAsync(supplierId, (productId, 3m, 4m));

        var detail = (await ReadBody(await Client.GetAsync($"/api/v1/purchases/{purchase.GetProperty("id").GetInt32()}")))
            .GetProperty("data");
        Assert.Equal(purchase.GetProperty("orderNo").GetString(), detail.GetProperty("orderNo").GetString());
        Assert.False(detail.GetProperty("isVoided").GetBoolean());

        // 明细键名是 items（与销售单详情一致），不是 details
        var line = detail.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(name, line.GetProperty("name").GetString());
        Assert.Equal(3m, line.GetProperty("qty").GetDecimal());
        Assert.Equal(4m, line.GetProperty("costPrice").GetDecimal());
        Assert.Equal(12m, line.GetProperty("subTotal").GetDecimal());

        var missing = await ReadBody(await Client.GetAsync("/api/v1/purchases/999999"));
        Assert.NotEqual(0, missing.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 进货单编辑_先回退原明细再按新明细入库且单据号不变()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITDPE");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 4m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        var purchase = await SeedPurchaseAsync(supplierId, (productId, 5m, 4m));
        var purchaseId = purchase.GetProperty("id").GetInt32();
        var orderNo = purchase.GetProperty("orderNo").GetString()!;
        Assert.Equal(5m, await StockOfAsync(name));

        // 改成 8 件：应先回退 5、再入库 8 ⇒ 净 8，而不是 13（成本价同步按移动加权平均重算）
        var resp = await PutJsonAsync($"/api/v1/purchases/{purchaseId}", new
        {
            supplierId,
            details = new[] { new { productId, qty = 8m, costPrice = 4.5m } },
            totalQty = 8m,
            totalAmount = 36m,
            remark = "改过数量",
        });
        Assert.Equal(0, (await ReadBody(resp)).GetProperty("code").GetInt32());
        Assert.Equal(8m, await StockOfAsync(name));

        // 这一单是**就地改**：单据号不变、也没有被打上作废标记
        //（不是「作废旧单 + 新建新单」，所以列表里不会多出一张作废单）
        var rows = (await ReadBody(await Client.GetAsync($"/api/v1/purchases?keyword={orderNo}")))
            .GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        var row = rows.Single(x => x.GetProperty("orderNo").GetString() == orderNo);
        Assert.False(row.GetProperty("isVoided").GetBoolean());
        Assert.Equal(8m, row.GetProperty("totalQty").GetDecimal());
        Assert.Equal(36m, row.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("改过数量", row.GetProperty("remark").GetString());
    }

    [Fact]
    public async Task 进货单编辑_不存在的单号返回业务失败()
    {
        await LoginAsAdminAsync();
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));
        var productId = await SeedProductAsync(NewTag("ITDPE2"), salePrice: 9m, costPrice: 4m);

        var resp = await PutJsonAsync("/api/v1/purchases/999999", new
        {
            supplierId,
            details = new[] { new { productId, qty = 1m, costPrice = 4m } },
            totalQty = 1m,
            totalAmount = 4m,
        });
        Assert.NotEqual(0, (await ReadBody(resp)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 进货单批量建单_一次建多张且库存正确累加()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITDPB");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 4m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));

        object One(decimal qty) => new
        {
            supplierId,
            details = new[] { new { productId, qty, costPrice = 4m } },
            totalQty = qty,
            totalAmount = qty * 4m,
        };

        var body = await ReadBody(await PostJsonAsync("/api/v1/purchases/batch", new[] { One(2m), One(3m) }));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        // 响应形状是 {created:[{id,orderNo},…], total:n} —— created 是**数组**（每张新建单的主键与单号）；
        // 没有 failed 字段：任一张不合法就整包拒绝（见下一条用例）
        var data = body.GetProperty("data");
        var created = data.GetProperty("created").EnumerateArray().ToList();
        Assert.Equal(2, created.Count);
        Assert.Equal(2, data.GetProperty("total").GetInt32());
        Assert.All(created, x => Assert.False(string.IsNullOrWhiteSpace(x.GetProperty("orderNo").GetString())));
        // 同批两张单不能撞号（单号按天计数生成，批量建单最容易在这里踩坑）
        Assert.Equal(2, created.Select(x => x.GetProperty("orderNo").GetString()).Distinct().Count());
        Assert.Equal(5m, await StockOfAsync(name));
    }

    [Fact]
    public async Task 进货单批量建单_任一张不合法则一张都不写()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITDPB2");
        var productId = await SeedProductAsync(name, salePrice: 9m, costPrice: 4m);
        var supplierId = await SeedSupplierAsync(NewTag("ITSUP"));

        var body = await ReadBody(await PostJsonAsync("/api/v1/purchases/batch", new object[]
        {
            new
            {
                supplierId,
                details = new[] { new { productId, qty = 2m, costPrice = 4m } },
                totalQty = 2m,
                totalAmount = 8m,
            },
            // 第二张的商品不存在 ⇒ 整包拒绝、第一张也不许入库
            new
            {
                supplierId,
                details = new[] { new { productId = 999999, qty = 1m, costPrice = 4m } },
                totalQty = 1m,
                totalAmount = 4m,
            },
        }));

        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        // 前置校验失败 ⇒ 零写入。InMemory 下事务是 no-op，整包语义里只有这一条可测。
        Assert.Equal(0m, await StockOfAsync(name));
    }
}
