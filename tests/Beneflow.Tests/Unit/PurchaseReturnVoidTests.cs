using Beneflow.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 采购退货作废：回补库存 → 写流水 → 标记作废。
///
/// 三件要守住的事：
/// ① **只回补数量，不动成本价** —— 退货那一刻也没重算移动加权成本（只减了 StockQuantity），
///    所以数量加回去就完全还原了；顺手"修正"成本价反而会把账搞乱。
/// ② 重复作废只报错、不重复回补库存（判定在库存锁内，并发作废会被串行化）。
/// ③ 作废后单据仍在列表里（带「已作废」状态），但不进合计、不进供应商对账。
/// </summary>
public class PurchaseReturnVoidTests : TestBase
{
    /// <summary>备好库存后走服务建一张采购退货单</summary>
    private async Task<(int returnId, string orderNo)> CreateReturnAsync(decimal qty, decimal cost = 5m, decimal stock = 10m)
    {
        SetStock(ProductAId, stock, cost);
        var r = await PurchaseSvc.CreateReturnAsync(new CreatePurchaseReturnDto
        {
            SupplierId = SupplierId,
            Items = new List<PurchaseReturnItemDto>
            {
                new() { ProductId = ProductAId, Qty = qty, CostPrice = cost },
            },
        });
        Assert.Equal(0, r.Code);
        return (GetResultDataProp<int>(r.Data!, "id"), GetResultDataProp<string>(r.Data!, "orderNo")!);
    }

    [Fact]
    public async Task VoidReturnAsync_回补库存并标记作废()
    {
        var (id, _) = await CreateReturnAsync(4);
        Assert.Equal(6m, GetProduct(ProductAId).StockQuantity);   // 退货出库 4

        var r = await PurchaseSvc.VoidReturnAsync(id);

        Assert.Equal(0, r.Code);
        Assert.Equal(10m, GetProduct(ProductAId).StockQuantity);  // 作废把 4 件加回来

        var ret = await Db.PurchaseReturns.AsNoTracking().FirstAsync(x => x.Id == id);
        Assert.True(ret.IsVoided);
        Assert.NotNull(ret.VoidedAt);
        Assert.Equal(20m, ret.RefundAmount);                      // 应退金额不变（作废不改历史金额）
    }

    [Fact]
    public async Task VoidReturnAsync_不动成本价()
    {
        var (id, _) = await CreateReturnAsync(4, cost: 5m);
        var costBefore = GetProduct(ProductAId).CostPrice;

        await PurchaseSvc.VoidReturnAsync(id);

        Assert.Equal(costBefore, GetProduct(ProductAId).CostPrice);
    }

    [Fact]
    public async Task VoidReturnAsync_重复作废_只报错不重复回补()
    {
        var (id, _) = await CreateReturnAsync(4);

        Assert.Equal(0, (await PurchaseSvc.VoidReturnAsync(id)).Code);
        var second = await PurchaseSvc.VoidReturnAsync(id);

        Assert.NotEqual(0, second.Code);
        Assert.Contains("已作废", second.Message);
        Assert.Equal(10m, GetProduct(ProductAId).StockQuantity);   // 没有被加第二次
    }

    [Fact]
    public async Task VoidReturnAsync_单据不存在_返回失败()
    {
        var r = await PurchaseSvc.VoidReturnAsync(999999);

        Assert.NotEqual(0, r.Code);
        Assert.Contains("不存在", r.Message);
    }

    [Fact]
    public async Task VoidReturnAsync_写库存流水与操作日志()
    {
        var (id, orderNo) = await CreateReturnAsync(4);

        await PurchaseSvc.VoidReturnAsync(id);

        var log = await Db.StockLogs.AsNoTracking()
            .Where(s => s.ProductId == ProductAId && s.ChangeType == "采购退货作废回补")
            .OrderByDescending(s => s.Id).FirstAsync();
        Assert.Equal(4m, log.ChangeQty);
        Assert.Equal(6m, log.BeforeQty);
        Assert.Equal(10m, log.AfterQty);
        Assert.Equal(orderNo, log.RefNo);   // 流水回指退货单号，事后能顺着查

        var op = await Db.OperationLogs.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync();
        Assert.Equal("采购管理", op.Module);
        Assert.Equal("作废采购退货单", op.Action);
        Assert.Contains(orderNo, op.Target);
        Assert.Contains("回补库存 4 件", op.Target);
    }

    [Fact]
    public async Task VoidReturnAsync_作废后可以重新退货()
    {
        var (id, _) = await CreateReturnAsync(4);
        await PurchaseSvc.VoidReturnAsync(id);

        // 库存已回到 10，所以同样的 4 件能再退一次（作废的意义就在这：退错了能改）
        var again = await PurchaseSvc.CreateReturnAsync(new CreatePurchaseReturnDto
        {
            SupplierId = SupplierId,
            Items = new List<PurchaseReturnItemDto> { new() { ProductId = ProductAId, Qty = 4, CostPrice = 5m } },
        });

        Assert.Equal(0, again.Code);
        Assert.Equal(6m, GetProduct(ProductAId).StockQuantity);
    }

    [Fact]
    public async Task ReturnListAsync_带作废状态与剔除作废的合计()
    {
        var (voidedId, _) = await CreateReturnAsync(2);            // 应退 10
        var (keptId, _) = await CreateReturnAsync(3);              // 应退 15
        await PurchaseSvc.VoidReturnAsync(voidedId);

        var page = await PurchaseSvc.ReturnListAsync(null, 1, 20);

        Assert.Equal(2, page.Total);   // 作废单照常列出（可追溯），只是不进合计
        var kept = page.List.Single(x => Prop<int>(x, "id") == keptId);
        var voided = page.List.Single(x => Prop<int>(x, "id") == voidedId);
        Assert.Equal("已退货", Prop<string>(kept, "status"));
        Assert.Equal("已作废", Prop<string>(voided, "status"));
        Assert.False(Prop<bool>(kept, "isVoided"));
        Assert.True(Prop<bool>(voided, "isVoided"));

        Assert.Equal(1, Prop<int>(page.Summary, "returns"));
        Assert.Equal(15m, Prop<decimal>(page.Summary, "refundAmount"));
    }
}
