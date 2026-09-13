using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 销售管理单元测试：收银结算、作废回补、销售退货、赊账还款与统计。
/// 重点验证：金额以后端售价为准重算、库存扣减与回补、流水、可退数量核销、欠款结清。
/// </summary>
public class SaleServiceTests : TestBase
{
    /// <summary>直接设置商品库存/成本，跳过采购流程（TestBase.SetStock）</summary>
    private static SaleItemDto Item(int productId, decimal qty, decimal unitPrice = 0) =>
        new() { ProductId = productId, Qty = qty, UnitPrice = unitPrice };

    private async Task<(int orderId, string orderNo)> CreateSaleAsync(decimal qty, string payMethod = "微信",
        decimal cash = 0, decimal discount = 0, bool isCredit = false, string? wechat = null, decimal unitPrice = 0)
    {
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, qty, unitPrice) },
            PayMethod = payMethod, CashAmount = cash, DiscountAmount = discount,
            IsCredit = isCredit, WechatId = wechat,
        });
        Assert.Equal(0, r.Code);
        return (GetResultDataProp<int>(r.Data!, "id"), GetResultDataProp<string>(r.Data!, "orderNo")!);
    }

    // ================= 收银结算 =================

    [Fact]
    public async Task CreateAsync_CashSale_StockDeductedAndChangeCorrect()
    {
        SetStock(ProductAId, 10, 5.00m);   // 售价 10，成本 5

        var (orderId, _) = await CreateSaleAsync(2, "现金", cash: 25);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(20.00m, o.TotalAmount);
        Assert.Equal(20.00m, o.PayAmount);
        Assert.Equal(25.00m, o.CashAmount);
        Assert.Equal(5.00m, o.ChangeAmount);
        Assert.Equal(8, GetProduct(ProductAId).StockQuantity);

        var log = LastStockLog(ProductAId)!;
        Assert.Equal("销售出库", log.ChangeType);
        Assert.Equal(-2, log.ChangeQty);
        Assert.Equal(10, log.BeforeQty);
        Assert.Equal(8, log.AfterQty);
    }

    [Fact]
    public async Task CreateAsync_AmountRecalculatedByDbPrice()
    {
        SetStock(ProductAId, 10);
        // 前端传了 999 的单价，后端必须按数据库售价 10 重算
        var (orderId, _) = await CreateSaleAsync(3, "微信", unitPrice: 999);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(30.00m, o.TotalAmount);
        Assert.Equal(30.00m, o.PayAmount);
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);
        Assert.Equal(10.00m, detail.UnitPrice);
        Assert.Equal(5.00m, detail.CostPrice);   // 成本快照用于毛利
    }

    [Fact]
    public async Task CreateAsync_DiscountClampedToTotal()
    {
        SetStock(ProductAId, 10);
        var (orderId, _) = await CreateSaleAsync(2, "微信", discount: 100);  // 优惠超过总额
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(20.00m, o.DiscountAmount);
        Assert.Equal(0m, o.PayAmount);
    }

    [Fact]
    public async Task CreateAsync_NegativeDiscount_TreatedAsZero()
    {
        SetStock(ProductAId, 10);
        var (orderId, _) = await CreateSaleAsync(2, "微信", discount: -5);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(0m, o.DiscountAmount);
        Assert.Equal(20.00m, o.PayAmount);
    }

    [Fact]
    public async Task CreateAsync_CashInsufficient_ReturnsError()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 2) },
            PayMethod = "现金", CashAmount = 5,
        });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("实收金额不足", r.Message);
    }

    [Fact]
    public async Task CreateAsync_StockInsufficient_ReturnsError()
    {
        SetStock(ProductAId, 1);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 5) },
            PayMethod = "微信",
        });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("库存不足", r.Message);
    }

    [Fact]
    public async Task CreateAsync_EmptyItems_ReturnsError()
    {
        var r = await SaleSvc.CreateAsync(new CreateSaleDto { Items = new List<SaleItemDto>() });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("购物清单为空", r.Message);
    }

    [Fact]
    public async Task CreateAsync_NonWeightedDecimalQty_ReturnsError()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 1.5m) },
            PayMethod = "微信",
        });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("整数", r.Message);
    }

    [Fact]
    public async Task CreateAsync_WeightedProduct_DecimalQtyAllowed()
    {
        SetStock(ProductAId, 10);
        var p = Db.Products.First(x => x.Id == ProductAId);
        p.IsWeighted = true;
        await Db.SaveChangesAsync();

        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 1.5m) },
            PayMethod = "微信",
        });
        Assert.Equal(0, r.Code);
        Assert.Equal(8.5m, GetProduct(ProductAId).StockQuantity);
    }

    [Fact]
    public async Task CreateAsync_SameProductRows_Merged()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 2), Item(ProductAId, 3) },
            PayMethod = "微信",
        });
        var orderId = GetResultDataProp<int>(r.Data!, "id");

        var details = await Db.SaleOrderDetails.AsNoTracking().Where(d => d.OrderId == orderId).ToListAsync();
        Assert.Single(details);
        Assert.Equal(5, details[0].Quantity);
        Assert.Equal(5, GetProduct(ProductAId).StockQuantity);
    }

    // ================= 赊账 =================

    [Fact]
    public async Task CreateAsync_CreditSale_CreatesCreditRecord()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 2) },
            PayMethod = "赊账", WechatId = "wxid_001", Phone = "13800138000",
        });
        Assert.Equal(0, r.Code);
        var orderId = GetResultDataProp<int>(r.Data!, "id");

        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);
        Assert.True(o.IsCredit);           // 支付方式为「赊账」时自动置赊账标记
        Assert.Equal("wxid_001", o.WechatId);

        var credit = await Db.CreditSales.AsNoTracking().FirstAsync(c => c.SaleOrderId == orderId);
        Assert.Equal(20.00m, credit.CreditAmount);
        Assert.Equal(20.00m, credit.RemainingAmount);
        Assert.False(credit.Status);
    }

    [Fact]
    public async Task CreateAsync_CreditDisabledByConfig_ReturnsError()
    {
        SetStock(ProductAId, 10);
        SetConfig("sale", "{\"allowCredit\":false}");

        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 1) },
            PayMethod = "赊账", IsCredit = true, WechatId = "wxid_001",
        });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("不允许赊账", r.Message);
    }

    [Fact]
    public async Task CreateAsync_NonCreditSale_WechatIdNotSaved()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 1) },
            PayMethod = "现金", CashAmount = 10, IsCredit = false, WechatId = "wxid_ignored",
        });
        var orderId = GetResultDataProp<int>(r.Data!, "id");
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);
        Assert.Null(o.WechatId);
        Assert.Empty(Db.CreditSales.Where(c => c.SaleOrderId == orderId));
    }

    // ================= 作废销售单 =================

    [Fact]
    public async Task VoidAsync_RestockAndWriteLog()
    {
        SetStock(ProductAId, 10);
        var (orderId, orderNo) = await CreateSaleAsync(3);
        Assert.Equal(7, GetProduct(ProductAId).StockQuantity);

        var r = await SaleSvc.VoidAsync(orderId);
        Assert.Equal(0, r.Code);

        Assert.True((await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId)).IsVoided);
        Assert.Equal(10, GetProduct(ProductAId).StockQuantity);

        var log = LastStockLog(ProductAId)!;
        Assert.Equal("作废回补", log.ChangeType);
        Assert.Equal(3, log.ChangeQty);
        Assert.Equal(orderNo, log.RefNo);
    }

    [Fact]
    public async Task VoidAsync_AlreadyVoided_ReturnsError()
    {
        SetStock(ProductAId, 10);
        var (orderId, _) = await CreateSaleAsync(1);
        await SaleSvc.VoidAsync(orderId);

        var again = await SaleSvc.VoidAsync(orderId);
        Assert.NotEqual(0, again.Code);
        Assert.Contains("已作废", again.Message);
    }

    [Fact]
    public async Task VoidAsync_OrderWithReturnedQty_ReturnsError()
    {
        SetStock(ProductAId, 10);
        var (orderId, orderNo) = await CreateSaleAsync(5);
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);

        await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 2 } },
        });

        var r = await SaleSvc.VoidAsync(orderId);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("已发生退货", r.Message);
    }

    [Fact]
    public async Task VoidAsync_CreditOrder_CreditCleared()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 2) },
            PayMethod = "赊账", WechatId = "wxid_002",
        });
        var orderId = GetResultDataProp<int>(r.Data!, "id");

        await SaleSvc.VoidAsync(orderId);

        var credit = await Db.CreditSales.AsNoTracking().FirstAsync(c => c.SaleOrderId == orderId);
        Assert.True(credit.Status);
        Assert.NotNull(credit.SettledAt);
        Assert.Contains("作废", credit.Remark!);
    }

    // ================= 销售退货 =================

    [Fact]
    public async Task CreateReturnAsync_PartialReturn_RestockAndRefund()
    {
        SetStock(ProductAId, 10);
        var (orderId, orderNo) = await CreateSaleAsync(5);
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);

        var r = await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 2 } },
        });
        Assert.Equal(0, r.Code);
        Assert.Equal(20.00m, GetResultDataProp<decimal>(r.Data!, "refundTotal"));

        Assert.Equal(7, GetProduct(ProductAId).StockQuantity);
        var after = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.Id == detail.Id);
        Assert.Equal(2, after.ReturnedQuantity);

        var log = LastStockLog(ProductAId)!;
        Assert.Equal("销售退货入库", log.ChangeType);
        Assert.Equal(2, log.ChangeQty);
    }

    [Fact]
    public async Task CreateReturnAsync_ExceedRemainingQty_ReturnsError()
    {
        SetStock(ProductAId, 10);
        var (orderId, orderNo) = await CreateSaleAsync(2);
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);

        var r = await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 5 } },
        });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("可退数量仅剩", r.Message);
    }

    [Fact]
    public async Task CreateReturnAsync_AllReturned_CannotReturnAgain()
    {
        SetStock(ProductAId, 10);
        var (orderId, orderNo) = await CreateSaleAsync(2);
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);

        var first = await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 2 } },
        });
        Assert.Equal(0, first.Code);

        var second = await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 1 } },
        });
        Assert.NotEqual(0, second.Code);
        Assert.Contains("整单退货", second.Message);
    }

    [Fact]
    public async Task CreateReturnAsync_OriginalOrderNotExist_ReturnsError()
    {
        var r = await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = "SO-NOPE",
            Items = new List<SaleReturnItemDto> { new() { Name = "商品A（无有效期）", Qty = 1 } },
        });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("原销售单不存在", r.Message);
    }

    [Fact]
    public async Task CreateReturnAsync_VoidedOrder_ReturnsError()
    {
        SetStock(ProductAId, 10);
        var (orderId, orderNo) = await CreateSaleAsync(2);
        await SaleSvc.VoidAsync(orderId);

        var r = await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { Name = "商品A（无有效期）", Qty = 1 } },
        });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("已作废", r.Message);
    }

    [Fact]
    public async Task CreateReturnAsync_CreditOrder_OffsetsRemainingAmount()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 5) },
            PayMethod = "赊账", WechatId = "wxid_003",
        });
        var orderId = GetResultDataProp<int>(r.Data!, "id");
        var orderNo = GetResultDataProp<string>(r.Data!, "orderNo")!;
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);

        // 退 2 件 × 10 元 = 20 元，冲抵欠款 50 → 30
        await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 2 } },
        });

        var credit = await Db.CreditSales.AsNoTracking().FirstAsync(c => c.SaleOrderId == orderId);
        Assert.Equal(20.00m, credit.PaidAmount);
        Assert.Equal(30.00m, credit.RemainingAmount);
        Assert.False(credit.Status);
    }

    [Fact]
    public async Task ReturnListAsync_ReturnsCreatedReturnOrder()
    {
        SetStock(ProductAId, 10);
        var (orderId, orderNo) = await CreateSaleAsync(3);
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);
        await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 1 } },
        });

        var page = await SaleSvc.ReturnListAsync(null, 1, 20);
        Assert.Equal(1, page.Total);
        Assert.Equal(10.00m, Prop<decimal>(page.List[0], "refundAmount"));   // 退 1 件 × 10 元
        Assert.Contains("SR", Prop<string>(page.List[0], "orderNo")!);
    }

    // ================= 赊账还款 =================

    private async Task<int> CreateCreditAsync(decimal qty)
    {
        SetStock(ProductAId, 100);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, qty) },
            PayMethod = "赊账", WechatId = "wxid_pay",
        });
        return (await Db.CreditSales.AsNoTracking()
            .FirstAsync(c => c.SaleOrderId == GetResultDataProp<int>(r.Data!, "id"))).Id;
    }

    [Fact]
    public async Task SettleAsync_PartialPayment_KeepsUnsettled()
    {
        var creditId = await CreateCreditAsync(10);   // 欠款 100

        var r = await SaleSvc.SettleAsync(creditId, new SettleCreditDto { PayAmount = 30, PayMethod = "微信" }, "127.0.0.1");
        Assert.Equal(0, r.Code);

        var c = await Db.CreditSales.AsNoTracking().FirstAsync(x => x.Id == creditId);
        Assert.Equal(30.00m, c.PaidAmount);
        Assert.Equal(70.00m, c.RemainingAmount);
        Assert.False(c.Status);
        Assert.True(await Db.CreditPayments.AsNoTracking().AnyAsync(p => p.CreditSaleId == creditId));
    }

    [Fact]
    public async Task SettleAsync_ZeroPayAmount_FullSettle()
    {
        var creditId = await CreateCreditAsync(4);    // 欠款 40

        var r = await SaleSvc.SettleAsync(creditId, new SettleCreditDto { PayAmount = 0 }, "127.0.0.1");
        Assert.Equal(0, r.Code);

        var c = await Db.CreditSales.AsNoTracking().FirstAsync(x => x.Id == creditId);
        Assert.Equal(40.00m, c.PaidAmount);
        Assert.Equal(0m, c.RemainingAmount);
        Assert.True(c.Status);
        Assert.NotNull(c.SettledAt);

        var op = await Db.OperationLogs.AsNoTracking()
            .OrderByDescending(l => l.Id).FirstAsync(l => l.Module == "赊账管理");
        Assert.Equal("结清欠款", op.Action);
    }

    [Fact]
    public async Task SettleAsync_ExceedRemaining_ReturnsError()
    {
        var creditId = await CreateCreditAsync(1);    // 欠款 10
        var r = await SaleSvc.SettleAsync(creditId, new SettleCreditDto { PayAmount = 50 }, "127.0.0.1");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("不能超过剩余欠款", r.Message);
    }

    [Fact]
    public async Task SettleAsync_AlreadySettled_ReturnsError()
    {
        var creditId = await CreateCreditAsync(1);
        await SaleSvc.SettleAsync(creditId, new SettleCreditDto(), "127.0.0.1");

        var again = await SaleSvc.SettleAsync(creditId, new SettleCreditDto(), "127.0.0.1");
        Assert.NotEqual(0, again.Code);
        Assert.Contains("已结清", again.Message);
    }

    [Fact]
    public async Task SettleAsync_NotFound_ReturnsError()
    {
        var r = await SaleSvc.SettleAsync(9999, new SettleCreditDto(), "127.0.0.1");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("赊账记录不存在", r.Message);
    }

    [Fact]
    public async Task UpdateCreditAsync_UpdatesPhoneAndRemark()
    {
        var creditId = await CreateCreditAsync(2);
        var r = await SaleSvc.UpdateCreditAsync(creditId, new UpdateCreditDto { Phone = "13911112222", Remark = "老顾客" });
        Assert.Equal(0, r.Code);

        var c = await Db.CreditSales.AsNoTracking().FirstAsync(x => x.Id == creditId);
        Assert.Equal("13911112222", c.Phone);
        Assert.Equal("老顾客", c.Remark);
    }

    [Fact]
    public async Task CreditListAsync_StatsCorrect()
    {
        SetStock(ProductAId, 100);
        await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 3) },
            PayMethod = "赊账", WechatId = "wxid_a",
        });
        await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 2) },
            PayMethod = "赊账", WechatId = "wxid_b",
        });

        var result = await SaleSvc.CreditListAsync(null, null, null, null, 1, 20);
        var stats = Prop(result, "stats")!;
        Assert.Equal(50.00m, Prop<decimal>(stats, "totalCredit"));
        Assert.Equal(50.00m, Prop<decimal>(stats, "unpaidTotal"));
        Assert.Equal(2, Prop<int>(stats, "unsettled"));
        Assert.Equal(0, Prop<int>(stats, "settled"));
        Assert.Equal(2, Prop<int>(result, "total"));
    }

    [Fact]
    public async Task CreditListAsync_FilterByStatus()
    {
        var settled = await CreateCreditAsync(1);
        await SaleSvc.SettleAsync(settled, new SettleCreditDto(), "127.0.0.1");
        await CreateCreditAsync(2);

        var unpaid = await SaleSvc.CreditListAsync(null, "未结清", null, null, 1, 20);
        Assert.Equal(1, Prop<int>(unpaid, "total"));

        var paid = await SaleSvc.CreditListAsync(null, "已结清", null, null, 1, 20);
        Assert.Equal(1, Prop<int>(paid, "total"));
    }

    // ================= 列表与详情状态 =================

    [Fact]
    public async Task ListAsync_StatusText_ByReturnedQty()
    {
        SetStock(ProductAId, 100);
        var (orderId, orderNo) = await CreateSaleAsync(4);
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);

        var page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);
        Assert.Equal("已完成", Prop<string>(page.List[0], "status"));

        await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 1 } },
        });
        page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);
        Assert.Equal("部分退货", Prop<string>(page.List[0], "status"));

        await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 3 } },
        });
        page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);
        Assert.Equal("已退货", Prop<string>(page.List[0], "status"));
    }

    [Fact]
    public async Task ListAsync_VoidedOrder_StatusIsVoided()
    {
        SetStock(ProductAId, 10);
        var (orderId, _) = await CreateSaleAsync(1);
        await SaleSvc.VoidAsync(orderId);

        var page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);
        Assert.Equal("已作废", Prop<string>(page.List[0], "status"));
    }

    [Fact]
    public async Task GetDetailByOrderNoAsync_ReturnsItems()
    {
        SetStock(ProductAId, 10);
        var (_, orderNo) = await CreateSaleAsync(2);

        var r = await SaleSvc.GetDetailByOrderNoAsync(orderNo);
        Assert.Equal(0, r.Code);
        var items = Prop<IEnumerable<object>>(r.Data, "items")!.ToList();
        Assert.Single(items);
        Assert.Equal(2m, Prop<decimal>(items[0], "qty"));
    }

    [Fact]
    public async Task GetDetailAsync_NotExist_ReturnsError()
    {
        var r = await SaleSvc.GetDetailAsync(9999);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("销售单不存在", r.Message);
    }
}
