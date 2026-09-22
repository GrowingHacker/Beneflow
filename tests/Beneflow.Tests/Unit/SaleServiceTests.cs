using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 销售管理单元测试：收银结算、作废回补、销售退货、赊账还款与统计。
/// 重点验证：金额以后端售价为准重算、库存扣减与回补、流水、可退数量核销、欠款结清。
/// </summary>
public class SaleServiceTests : TestBase
{
    /// <summary>直接设置商品库存/成本，跳过采购流程（TestBase.SetStock）</summary>
    private static SaleItemDto Item(int productId, decimal qty, decimal unitPrice = 0) =>
        new() { ProductId = productId, Qty = qty, UnitPrice = unitPrice };

    /// <summary>
    /// 收银结算并断言成功。让利超出系统设置限额时（整单优惠 > ¥50、低于 9 折、现金抹零 > ¥1）
    /// 要传 <paramref name="authPwd"/> = 店主密码，否则会被让利风控拦下 ——
    /// 本类里传了密码的用例，都是在模拟「店主授权放行的超额让利」，风控本身另见 SaleDiscountFenceTests。
    /// </summary>
    private async Task<(int orderId, string orderNo)> CreateSaleAsync(decimal qty, string payMethod = "微信",
        decimal cash = 0, decimal discount = 0, bool isCredit = false, string? wechat = null, decimal unitPrice = 0,
        decimal roundOff = 0, decimal? rate = null, string? authPwd = null)
    {
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, qty, unitPrice) },
            PayMethod = payMethod, CashAmount = cash, DiscountAmount = discount, DiscountRate = rate,
            RoundOffAmount = roundOff, IsCredit = isCredit, WechatId = wechat,
            DiscountAuthPassword = authPwd,
        });
        Assert.Equal(0, r.Code);
        return (GetResultDataProp<int>(r.Data!, "id"), GetResultDataProp<string>(r.Data!, "orderNo")!);
    }

    /// <summary>直接改商品售价：后端按数据库售价重算总额，要造出特定总额（尤其是「分位为 5」的）只能改这里</summary>
    private void SetSalePrice(int productId, decimal price)
    {
        var p = Db.Products.First(x => x.Id == productId);
        p.SalePrice = price;
        Db.SaveChanges();
    }

    private async Task<SaleOrder> Reload(int orderId) =>
        await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

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

    // ================= 档案优惠（优惠方案只在商品档案里定义，收银台只执行） =================

    /// <summary>直接设置商品档案优惠（跳过商品档案页）</summary>
    private void SetPromo(int productId, string type, decimal price = 0, decimal rate = 0,
        bool enabled = true, DateTime? startAt = null, DateTime? endAt = null)
    {
        var p = Db.Products.First(x => x.Id == productId);
        p.PromoType = type; p.PromoPrice = price; p.PromoRate = rate;
        p.PromoEnabled = enabled; p.PromoStartAt = startAt; p.PromoEndAt = endAt;
        Db.SaveChanges();
    }

    [Fact]
    public async Task CreateAsync_ProductPricePromo_ChargesPromoPriceAndSnapshotsBothPrices()
    {
        SetStock(ProductAId, 10);                                    // 挂牌价 10.00
        SetPromo(ProductAId, PromoHelper.TypePrice, price: 7.50m);

        var (orderId, _) = await CreateSaleAsync(3, "微信", unitPrice: 999);   // 前端传的单价一律不采信
        var o = await Reload(orderId);

        Assert.Equal(30.00m, o.TotalAmount);      // 商品总额 = Σ(挂牌价 × 数量) = 10.00 × 3（未优惠前）
        Assert.Equal(7.50m, o.DiscountAmount);    // 优惠 = 原价合计 − 成交合计 = 30.00 − 22.50
        Assert.Equal(22.50m, o.PayAmount);        // 应收 = 成交合计

        var d = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(x => x.OrderId == orderId);
        Assert.Equal(7.50m, d.UnitPrice);         // 成交价（退货退款基数）
        Assert.Equal(10.00m, d.OriginalPrice);    // 挂牌价快照（档案优惠日后会改，历史单靠它解释）
    }

    [Fact]
    public async Task CreateAsync_ExpiredPromo_ChargesSalePrice()
    {
        SetStock(ProductAId, 10);
        SetPromo(ProductAId, PromoHelper.TypePrice, price: 7.50m, endAt: DateTime.Now.AddDays(-1));

        var (orderId, _) = await CreateSaleAsync(3, "微信");
        var o = await Reload(orderId);

        Assert.Equal(30.00m, o.TotalAmount);      // 促销已过期 ⇒ 回到挂牌价
        Assert.Equal(0m, o.DiscountAmount);       // 没让利，优惠为 0

        var d = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(x => x.OrderId == orderId);
        Assert.Equal(10.00m, d.UnitPrice);
        Assert.Equal(10.00m, d.OriginalPrice);
    }

    [Fact]
    public async Task CreateAsync_RatePromo_ChargesRoundedPromoPrice()
    {
        SetStock(ProductAId, 10);
        SetPromo(ProductAId, PromoHelper.TypeRate, rate: 8.8m);      // 10.00 打 8.8 折 = 8.80

        var (orderId, _) = await CreateSaleAsync(2, "微信");
        var o = await Reload(orderId);

        Assert.Equal(20.00m, o.TotalAmount);      // 商品总额 = 10.00 × 2（未优惠前）
        Assert.Equal(2.40m, o.DiscountAmount);    // 8.8 折让掉 20.00 − 17.60
        Assert.Equal(17.60m, o.PayAmount);

        var d = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(x => x.OrderId == orderId);
        Assert.Equal(8.80m, d.UnitPrice);
        Assert.Equal(10.00m, d.OriginalPrice);
    }

    [Fact]
    public async Task CreateAsync_DiscountClampedToTotal()
    {
        SetStock(ProductAId, 10);
        var (orderId, _) = await CreateSaleAsync(2, "微信", discount: 100, authPwd: "123456");  // 优惠超过总额
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
        Assert.Contains("收款金额不足", r.Message);
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

    // ================= 金额口径（行业惯例） =================
    // 商品总额 → 优惠 → 抹零 → 应收 → 收款额(递钞) → 找零；实收 = 实际收到的净额

    [Fact]
    public async Task CreateAsync_CashSale_ReceivedIsNetNotTendered()
    {
        SetStock(ProductAId, 10, 5.00m);   // 售价 10

        var (orderId, _) = await CreateSaleAsync(2, "现金", cash: 25);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(20.00m, o.TotalAmount);
        Assert.Equal(0m, o.DiscountAmount);
        Assert.Equal(0m, o.RoundOffAmount);
        Assert.Equal(20.00m, o.PayAmount);        // 应收
        Assert.Equal(25.00m, o.CashAmount);       // 收款额（递钞），不是实收
        Assert.Equal(5.00m, o.ChangeAmount);      // 找零
        Assert.Equal(20.00m, o.ReceivedAmount);   // 实收 = 递钞 − 找零 = 应收
    }

    [Fact]
    public async Task CreateAsync_NonCashSale_CashAmountIsZero()
    {
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(2, "微信");
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        // 非现金单没有递钞与找零，收款额必须记 0（此前被错写成应收，会让导出的「收款额/现金」列虚高）
        Assert.Equal(20.00m, o.PayAmount);
        Assert.Equal(0m, o.CashAmount);
        Assert.Equal(0m, o.ChangeAmount);
        Assert.Equal(20.00m, o.ReceivedAmount);
    }

    [Fact]
    public async Task CreateAsync_CreditSale_ReceivedIsZero()
    {
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(2, "赊账", isCredit: true, wechat: "wx_credit");
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);
        var credit = await Db.CreditSales.AsNoTracking().FirstAsync(c => c.SaleOrderId == orderId);

        // 赊账为挂账：应收 20 但一分钱没收到，实收必须为 0；欠款额=应收
        Assert.Equal(20.00m, o.PayAmount);
        Assert.Equal(0m, o.ReceivedAmount);
        Assert.Equal(0m, o.CashAmount);
        Assert.Equal(20.00m, credit.CreditAmount);
        Assert.Equal(20.00m, credit.RemainingAmount);
    }

    [Fact]
    public async Task CreateAsync_CashRoundOff_ReducesPayableOnly()
    {
        SetStock(ProductAId, 10);   // 售价 10 → 总额 20

        // 抹零 3：应收 17，顾客递 20，找零 3（抹零 3 已超 ¥1 上限，故要店主授权）
        var (orderId, _) = await CreateSaleAsync(2, "现金", cash: 20, roundOff: 3, authPwd: "123456");
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(20.00m, o.TotalAmount);      // 商品总额不受抹零影响
        Assert.Equal(3.00m, o.RoundOffAmount);
        Assert.Equal(17.00m, o.PayAmount);        // 应收 = 总额 − 优惠 − 抹零
        Assert.Equal(3.00m, o.ChangeAmount);
        Assert.Equal(17.00m, o.ReceivedAmount);   // 实收 = 净收
    }

    [Fact]
    public async Task CreateAsync_NonCashRoundOff_Ignored()
    {
        SetStock(ProductAId, 10);

        // 行业惯例只对现金抹零：扫码支付传了抹零也要忽略
        var (orderId, _) = await CreateSaleAsync(2, "微信", roundOff: 3);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(0m, o.RoundOffAmount);
        Assert.Equal(20.00m, o.PayAmount);
        Assert.Equal(20.00m, o.ReceivedAmount);
    }

    [Fact]
    public async Task CreateAsync_RoundOffExceedsPayable_Clamped()
    {
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(2, "现金", cash: 0, roundOff: 999, authPwd: "123456");
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(20.00m, o.RoundOffAmount);   // 夹到折后金额
        Assert.Equal(0m, o.PayAmount);
        Assert.Equal(0m, o.ChangeAmount);
        Assert.Equal(0m, o.ReceivedAmount);
    }

    [Fact]
    public async Task CreateAsync_DiscountAndRoundOff_Chained()
    {
        SetStock(ProductAId, 10);   // 总额 20

        // 总额 20 → 优惠 4 → 折后 16 → 抹零 1 → 应收 15 → 递 20 → 找零 5
        var (orderId, _) = await CreateSaleAsync(2, "现金", cash: 20, discount: 4, roundOff: 1);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);

        Assert.Equal(20.00m, o.TotalAmount);
        Assert.Equal(4.00m, o.DiscountAmount);
        Assert.Equal(1.00m, o.RoundOffAmount);
        Assert.Equal(15.00m, o.PayAmount);
        Assert.Equal(5.00m, o.ChangeAmount);
        Assert.Equal(15.00m, o.ReceivedAmount);
    }

    // ========== 折扣率（打几折）：与金额法是同一条链上的两种录入方式 ==========

    [Fact]
    public async Task CreateAsync_ByRate_DiscountComputedBySubtraction()
    {
        SetSalePrice(ProductAId, 1.05m);   // 总额 1.05：9 折会让乘积正好落到「半分」上
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(1, "微信", rate: 9);
        var o = await Reload(orderId);

        // 折后 = round(1.05 × 0.9, 2) = 0.94（银行家舍入）；优惠由减法反算 = 1.05 − 0.94 = 0.11。
        // 若改成「正算 round(1.05 × 0.1)」会得到 0.10，恒等式就崩了 —— 这条用例专门钉住运算顺序。
        Assert.Equal(1.05m, o.TotalAmount);
        Assert.Equal(0.94m, o.PayAmount);
        Assert.Equal(0.11m, o.DiscountAmount);
        Assert.Equal(9m, o.DiscountRate);
        Assert.Equal(o.TotalAmount - o.DiscountAmount, o.PayAmount);
    }

    [Fact]
    public async Task CreateAsync_ByRate_StoredRateReproducesPayable()
    {
        SetStock(ProductAId, 10);   // 售价 10 → 总额 20

        var (orderId, _) = await CreateSaleAsync(2, "微信", rate: 8.8m, authPwd: "123456");
        var o = await Reload(orderId);

        Assert.Equal(20.00m, o.TotalAmount);
        Assert.Equal(2.40m, o.DiscountAmount);
        Assert.Equal(17.60m, o.PayAmount);
        Assert.Equal(8.80m, o.DiscountRate);

        // 折率是可独立校验的：拿落库的折率重算折后金额应与落库值一致（按金额录入做不到这一点，
        // 所以两者都给时后端以折率为准）
        Assert.Equal(o.PayAmount, Math.Round(o.TotalAmount * o.DiscountRate!.Value / 10m, 2));
    }

    [Fact]
    public async Task CreateAsync_ByRate_TenRate_NoDiscount()
    {
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(2, "微信", rate: 10);
        var o = await Reload(orderId);

        Assert.Equal(0.00m, o.DiscountAmount);
        Assert.Equal(20.00m, o.PayAmount);
        Assert.Equal(10m, o.DiscountRate);   // 10 折等价于不打折，但仍是「按折率录入」，故留痕迹
    }

    [Fact]
    public async Task CreateAsync_ByRate_LowerBoundAllowed()
    {
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(1, "微信", rate: 0.1m, authPwd: "123456");   // 0.1 折 = 原价的 1%
        var o = await Reload(orderId);

        Assert.Equal(9.90m, o.DiscountAmount);
        Assert.Equal(0.10m, o.PayAmount);
        Assert.Equal(0.1m, o.DiscountRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.05)]
    [InlineData(10.5)]
    [InlineData(-1)]
    public async Task CreateAsync_ByRate_OutOfRange_ReturnsError(decimal rate)
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 1) },
            PayMethod = "微信", DiscountRate = rate,
        });

        Assert.NotEqual(0, r.Code);
        Assert.Contains("折扣需在 0.1 ~ 10 折之间", r.Message);
        Assert.Empty(Db.SaleOrders);   // 越界在落库前就被拦下，不留半张单
    }

    [Fact]
    public async Task CreateAsync_ByRate_OverridesAmountField()
    {
        SetStock(ProductAId, 10);   // 总额 10

        var (orderId, _) = await CreateSaleAsync(1, "微信", discount: 9.99m, rate: 8, authPwd: "123456");
        var o = await Reload(orderId);

        // 两个都给时以折率为准，传进来的金额被忽略
        Assert.Equal(2.00m, o.DiscountAmount);
        Assert.Equal(8.00m, o.PayAmount);
        Assert.Equal(8m, o.DiscountRate);
    }

    [Fact]
    public async Task CreateAsync_ByRate_WithRoundOff_Chained()
    {
        SetStock(ProductAId, 10, 5.00m);   // 总额 20

        // 总额 20 → 8 折 → 折后 16 → 抹零 0.40 → 应收 15.60 → 递 20 → 找零 4.40 → 实收 15.60
        var (orderId, _) = await CreateSaleAsync(2, "现金", cash: 20, roundOff: 0.40m, rate: 8, authPwd: "123456");
        var o = await Reload(orderId);

        Assert.Equal(4.00m, o.DiscountAmount);
        Assert.Equal(0.40m, o.RoundOffAmount);
        Assert.Equal(15.60m, o.PayAmount);
        Assert.Equal(4.40m, o.ChangeAmount);
        Assert.Equal(15.60m, o.ReceivedAmount);
        Assert.Equal(8m, o.DiscountRate);
    }

    [Fact]
    public async Task CreateAsync_ByRate_CreditSale_ReceivedIsZero()
    {
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(1, "赊账", isCredit: true, wechat: "wx_rate", rate: 9);
        var o = await Reload(orderId);

        Assert.Equal(1.00m, o.DiscountAmount);
        Assert.Equal(9.00m, o.PayAmount);
        Assert.Equal(0m, o.ReceivedAmount);   // 开单挂账时实收为 0（还款后回写到该单）
        Assert.Equal(9m, o.DiscountRate);
    }

    [Fact]
    public async Task CreateAsync_AmountMode_DiscountRateIsNull()
    {
        SetStock(ProductAId, 10);

        var (orderId, _) = await CreateSaleAsync(2, "微信", discount: 3);
        var o = await Reload(orderId);

        // 按金额录入不留折率：null 表示「不是按折率录的」，与「按了 10 折」区分开
        Assert.Equal(3.00m, o.DiscountAmount);
        Assert.Null(o.DiscountRate);
    }

    [Fact]
    public async Task CreateAsync_ByRate_KeepsStockAndLogsRate()
    {
        SetStock(ProductAId, 10, 5.00m);

        var (orderId, _) = await CreateSaleAsync(2, "现金", cash: 20, rate: 5, authPwd: "123456");
        var log = await Db.OperationLogs.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync();

        Assert.Equal(10.00m, (await Reload(orderId)).DiscountAmount);
        Assert.Equal(8, GetProduct(ProductAId).StockQuantity);
        Assert.Contains("折扣 5 折", log.Target);   // 折率进日志：事后能区分「按折扣让利」与「手工抹了个数」
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

        // 还款回写原单实收：赊账单实收 = 累计已还款额；应收不变，未还的仍挂在欠款里
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == c.SaleOrderId);
        Assert.Equal(30.00m, o.ReceivedAmount);
        Assert.Equal(100.00m, o.PayAmount);
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

        // 结清后实收补齐到应收
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == c.SaleOrderId);
        Assert.Equal(40.00m, o.ReceivedAmount);
        Assert.Equal(o.PayAmount, o.ReceivedAmount);

        var op = await Db.OperationLogs.AsNoTracking()
            .OrderByDescending(l => l.Id).FirstAsync(l => l.Module == "赊账管理");
        Assert.Equal("结清欠款", op.Action);
    }

    [Fact]
    public async Task SettleAsync_MultiplePayments_ReceivedAmountAccumulates()
    {
        var creditId = await CreateCreditAsync(10);   // 欠款 100
        var saleOrderId = (await Db.CreditSales.AsNoTracking().FirstAsync(x => x.Id == creditId)).SaleOrderId;

        await SaleSvc.SettleAsync(creditId, new SettleCreditDto { PayAmount = 30 }, "127.0.0.1");
        await SaleSvc.SettleAsync(creditId, new SettleCreditDto { PayAmount = 20 }, "127.0.0.1");

        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == saleOrderId);
        Assert.Equal(50.00m, o.ReceivedAmount);        // 30 + 20
        Assert.Equal(100.00m, o.PayAmount);            // 应收自始至终不变
        Assert.Equal(50.00m, (await Db.CreditSales.AsNoTracking().FirstAsync(x => x.Id == creditId)).RemainingAmount);

        await SaleSvc.SettleAsync(creditId, new SettleCreditDto(), "127.0.0.1");   // 全额结清剩余 50

        o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == saleOrderId);
        Assert.Equal(100.00m, o.ReceivedAmount);       // 实收补齐到应收
        Assert.Equal(o.PayAmount, o.ReceivedAmount);
    }

    [Fact]
    public async Task SettleAsync_ReceivedAmount_ExcludesReturnOffset()
    {
        SetStock(ProductAId, 10);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 5) },   // 赊账 50
            PayMethod = "赊账", WechatId = "wxid_off",
        });
        var orderId = GetResultDataProp<int>(r.Data!, "id");
        var orderNo = GetResultDataProp<string>(r.Data!, "orderNo")!;
        var creditId = (await Db.CreditSales.AsNoTracking().FirstAsync(c => c.SaleOrderId == orderId)).Id;
        var detail = await Db.SaleOrderDetails.AsNoTracking().FirstAsync(d => d.OrderId == orderId);

        // 退 2 件 = 20 元，抵掉欠款但钱没到手 ⇒ 实收不动
        await SaleSvc.CreateReturnAsync(new CreateSaleReturnDto
        {
            OriginalOrderNo = orderNo,
            Items = new List<SaleReturnItemDto> { new() { DetailId = detail.Id, Qty = 2 } },
        });
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);
        Assert.Equal(0m, o.ReceivedAmount);

        // 再实还剩余 30 ⇒ 实收 = 真正收到的 30，而不是 50
        await SaleSvc.SettleAsync(creditId, new SettleCreditDto(), "127.0.0.1");

        o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.Id == orderId);
        Assert.Equal(30.00m, o.ReceivedAmount);
        Assert.Equal(50.00m, o.PayAmount);
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
    public async Task ListAsync_Row_CarriesIndustryAmountFields()
    {
        SetStock(ProductAId, 10);   // 售价 10 → 总额 20

        // 总额 20 → 抹零 2 → 应收 18 → 递 25 → 找零 7 → 实收 18
        await CreateSaleAsync(2, "现金", cash: 25, roundOff: 2, authPwd: "123456");
        var page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);
        var row = page.List[0];

        Assert.Equal(20m, Prop<decimal>(row, "totalAmount"));
        Assert.Equal(0m, Prop<decimal>(row, "discountAmount"));
        Assert.Equal(2m, Prop<decimal>(row, "roundOffAmount"));
        Assert.Equal(18m, Prop<decimal>(row, "payAmount"));
        Assert.Equal(18m, Prop<decimal>(row, "receivedAmount"));
        Assert.Equal(25m, Prop<decimal>(row, "cashAmount"));
        Assert.Equal(7m, Prop<decimal>(row, "changeAmount"));
    }

    [Fact]
    public async Task ListAsync_Summary_ExcludesVoidedOrders()
    {
        SetStock(ProductAId, 100);   // 售价 10
        var (voidedId, _) = await CreateSaleAsync(2, "现金", cash: 20);              // 应收 20 / 实收 20
        await CreateSaleAsync(1, "微信");                                            // 应收 10 / 实收 10
        await CreateSaleAsync(3, "赊账", isCredit: true, wechat: "wx_sum");           // 应收 30 / 实收 0
        Assert.Equal(0, (await SaleSvc.VoidAsync(voidedId)).Code);

        var page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);
        var summary = page.Summary!;

        // 合计与列表同过滤条件，但剔除已作废单据
        Assert.Equal(2, Prop<int>(summary, "orders"));
        Assert.Equal(40m, Prop<decimal>(summary, "totalAmount"));
        Assert.Equal(40m, Prop<decimal>(summary, "payAmount"));
        // 实收只算真正到手的钱：微信 10，赊账 30 挂账不计
        Assert.Equal(10m, Prop<decimal>(summary, "receivedAmount"));
    }

    [Fact]
    public async Task ListAsync_Summary_CoversCurrentFilterNotCurrentPage()
    {
        SetStock(ProductAId, 100);
        for (var i = 0; i < 3; i++) await CreateSaleAsync(1, "微信");   // 每单应收 10

        var page = await SaleSvc.ListAsync(null, null, null, null, 1, 1);   // 第 1 页只显示 1 条
        Assert.Single(page.List);
        Assert.Equal(30m, Prop<decimal>(page.Summary!, "payAmount"));       // 合计仍是全量 3 单
    }

    [Fact]
    public async Task ListAsync_Summary_EmptyResult_AllZero()
    {
        var page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);

        Assert.Empty(page.List);
        Assert.Equal(0, Prop<int>(page.Summary!, "orders"));
        Assert.Equal(0m, Prop<decimal>(page.Summary!, "receivedAmount"));
    }

    [Fact]
    public async Task ListAsync_Row_CarriesDiscountRate_SummaryOmitsIt()
    {
        SetStock(ProductAId, 100);
        await CreateSaleAsync(2, "微信", rate: 8.8m, authPwd: "123456");    // 总额 20 → 折后 17.60 → 优惠 2.40
        await CreateSaleAsync(1, "微信", discount: 3);   // 按金额录入

        var page = await SaleSvc.ListAsync(null, null, null, null, 1, 20);

        Assert.Null(Prop<object>(page.List[0], "discountRate"));      // 新的在前：按金额录入 → null
        Assert.Equal(8.80m, Prop<decimal>(page.List[1], "discountRate"));

        Assert.Equal(5.40m, Prop<decimal>(page.Summary!, "discountAmount"));
        // 折率不进合计：它是「率」不可加总（8 折 + 9 折没有意义），合计里连字段都不该出现
        Assert.Null(page.Summary!.GetType().GetProperty("discountRate"));
    }

    [Fact]
    public async Task ExportListAsync_DiscountRateCarriesUnit()
    {
        SetStock(ProductAId, 100);
        await CreateSaleAsync(2, "微信", rate: 8.8m, authPwd: "123456");
        await CreateSaleAsync(1, "微信", discount: 3);

        var rows = await SaleSvc.ExportListAsync(null, null, null, null);

        // 折率写成文本并带「折」单位：裸数字 8.8 会被读成 8.8%（或 88%），单位误会一次就错一次；
        // 未按折率录入的留空，与「按了 10 折」区分开
        Assert.Equal("", rows[0]["discountRate"]);
        Assert.Equal("8.8折", rows[1]["discountRate"]);
        Assert.Equal(3.00m, rows[0]["discountAmount"]);
        Assert.Equal(2.40m, rows[1]["discountAmount"]);
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

    // ================= 混合支付 =================
    //
    // 口径（与 SaleOrderPayment 注释一致）：
    //   已付金额 = Σ 非赊账行；挂账额 = 应收 − 已付（显式填「赊账」行时以该行为准，两者必须相等）
    //   找零 = 已付 − 应收，且只能来自现金（非现金合计不得超过应收）
    //   恒等式：Σ 支付明细 = 应收 + 找零

    /// <summary>构造混合支付的一行</summary>
    private static SalePaymentDto Pay(string method, decimal amount) =>
        new() { PayMethod = method, Amount = amount };

    /// <summary>以「商品A × qty」为购物清单走混合支付（Payments 非空即混合；商品A 售价 10）</summary>
    private Task<ApiResult<object>> MixedSaleAsync(decimal qty, params SalePaymentDto[] payments) =>
        SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, qty) },
            PayMethod = "混合", Payments = payments.ToList(),
        });

    private List<SaleOrderPayment> PaymentRows(int orderId) =>
        Db.SaleOrderPayments.AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.Id).ToList();

    [Fact]
    public async Task CreateAsync_MixedPayment_SplitsAmountsAndWritesComposition()
    {
        SetStock(ProductAId, 10, 5.00m);
        var r = await MixedSaleAsync(2, Pay("现金", 12m), Pay("微信", 8m));
        Assert.Equal(0, r.Code);
        var id = GetResultDataProp<int>(r.Data!, "id");
        var o = await Reload(id);

        Assert.Equal(20.00m, o.PayAmount);
        Assert.Equal("混合", o.PayMethod);          // 支付组成不止一种 ⇒ 记「混合」
        Assert.Equal(12.00m, o.CashAmount);        // 收款额＝现金那一部分
        Assert.Equal(0m, o.ChangeAmount);
        Assert.Equal(20.00m, o.ReceivedAmount);    // 实收 = 真正收到的钱
        Assert.False(o.IsCredit);

        var rows = PaymentRows(id);
        Assert.Equal(2, rows.Count);
        Assert.Equal(12.00m, rows.First(x => x.PayMethod == "现金").Amount);
        Assert.Equal(8.00m, rows.First(x => x.PayMethod == "微信").Amount);
        Assert.Equal(o.PayAmount + o.ChangeAmount, rows.Sum(x => x.Amount));   // 恒等式
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_CashOverpay_ProducesChange()
    {
        SetStock(ProductAId, 10, 5.00m);
        var r = await MixedSaleAsync(2, Pay("现金", 20m), Pay("微信", 7m));
        Assert.Equal(0, r.Code);
        var o = await Reload(GetResultDataProp<int>(r.Data!, "id"));

        Assert.Equal(20.00m, o.PayAmount);
        Assert.Equal(20.00m, o.CashAmount);        // 现金行填的是递钞额
        Assert.Equal(7.00m, o.ChangeAmount);       // 27 − 20
        Assert.Equal(20.00m, o.ReceivedAmount);    // 实收扣掉找零
        Assert.Equal(27.00m, PaymentRows(o.Id).Sum(x => x.Amount));   // = 应收 + 找零
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_Shortfall_BecomesCredit()
    {
        SetStock(ProductAId, 10, 5.00m);
        var r = await MixedSaleAsync(2, Pay("现金", 10m));   // 应收 20，只付了 10
        Assert.Equal(0, r.Code);
        var id = GetResultDataProp<int>(r.Data!, "id");
        var o = await Reload(id);

        Assert.Equal("混合", o.PayMethod);         // 现金 + 挂账 = 两种组成
        Assert.True(o.IsCredit);
        Assert.Equal(10.00m, o.ReceivedAmount);    // 只认到手的钱，挂账那 10 不算

        var credit = Db.CreditSales.First(c => c.SaleOrderId == id);
        Assert.Equal(10.00m, credit.CreditAmount);      // 欠的是差额，不是全额应收
        Assert.Equal(10.00m, credit.RemainingAmount);
        Assert.False(credit.Status);

        var rows = PaymentRows(id);
        Assert.Equal(10.00m, rows.First(x => x.PayMethod == "现金").Amount);
        Assert.Equal(10.00m, rows.First(x => x.PayMethod == "赊账").Amount);
        Assert.Equal(o.PayAmount + o.ChangeAmount, rows.Sum(x => x.Amount));
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_ExplicitCreditRowMustMatchShortfall()
    {
        SetStock(ProductAId, 10);
        var r = await MixedSaleAsync(2, Pay("现金", 10m), Pay("赊账", 5m));   // 10 + 5 ≠ 应收 20
        Assert.NotEqual(0, r.Code);
        Assert.Contains("与应收不符", r.Message);
        Assert.Empty(Db.SaleOrders);   // 校验失败不落单
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_CashlessCannotExceedPayable()
    {
        SetStock(ProductAId, 10);
        var r = await MixedSaleAsync(2, Pay("微信", 25m));   // 非现金 25 > 应收 20，找零不可能来自微信
        Assert.NotEqual(0, r.Code);
        Assert.Contains("不能超过应收金额", r.Message);
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_CreditRowWhenAlreadyPaidUp_Rejected()
    {
        SetStock(ProductAId, 10);
        var r = await MixedSaleAsync(2, Pay("现金", 20m), Pay("微信", 5m), Pay("赊账", 5m));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("无需再挂账", r.Message);
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_IgnoresRoundOff()
    {
        SetStock(ProductAId, 10, 5.00m);
        var r = await SaleSvc.CreateAsync(new CreateSaleDto
        {
            Items = new List<SaleItemDto> { Item(ProductAId, 2) },
            PayMethod = "混合", RoundOffAmount = 0.50m,
            Payments = new List<SalePaymentDto> { Pay("现金", 20m) },
        });
        Assert.Equal(0, r.Code);
        var o = await Reload(GetResultDataProp<int>(r.Data!, "id"));

        Assert.Equal(0m, o.RoundOffAmount);        // 混合支付不抹零
        Assert.Equal(20.00m, o.PayAmount);
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_SingleRow_StaysSingleMethod()
    {
        SetStock(ProductAId, 10, 5.00m);
        var r = await MixedSaleAsync(2, Pay("现金", 20m));
        var id = GetResultDataProp<int>(r.Data!, "id");

        Assert.Equal("现金", (await Reload(id)).PayMethod);   // 只有一种组成 ⇒ 与单项支付口径一致
        Assert.Empty(PaymentRows(id));                        // 也就不需要落支付明细
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_MergesSameMethodRows()
    {
        SetStock(ProductAId, 10);
        var r = await MixedSaleAsync(2, Pay("微信", 12m), Pay("微信", 8m));   // 同方式多行相加
        var id = GetResultDataProp<int>(r.Data!, "id");

        Assert.Equal("微信", (await Reload(id)).PayMethod);
        Assert.Empty(PaymentRows(id));
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_UnsupportedMethod_Rejected()
    {
        SetStock(ProductAId, 10);
        var r = await MixedSaleAsync(2, Pay("支票", 20m));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("不支持的支付方式", r.Message);
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_Shortfall_RejectedWhenCreditDisabled()
    {
        SetStock(ProductAId, 10);
        SetConfig("sale", "{\"allowCredit\":false}");
        var r = await MixedSaleAsync(2, Pay("现金", 10m));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("不允许赊账", r.Message);
    }

    [Fact]
    public async Task CreateAsync_MixedPayment_OnlyCreditRow_FullCredit()
    {
        SetStock(ProductAId, 10);
        var r = await MixedSaleAsync(2, Pay("赊账", 20m));
        Assert.Equal(0, r.Code);
        var id = GetResultDataProp<int>(r.Data!, "id");
        var o = await Reload(id);

        Assert.Equal("赊账", o.PayMethod);
        Assert.True(o.IsCredit);
        Assert.Equal(0m, o.ReceivedAmount);
        Assert.Equal(20.00m, Db.CreditSales.First(c => c.SaleOrderId == id).CreditAmount);
        Assert.Empty(PaymentRows(id));
    }

    [Fact]
    public async Task CreateAsync_SinglePayment_WritesNoCompositionRows()
    {
        SetStock(ProductAId, 10, 5.00m);
        var (id, _) = await CreateSaleAsync(2, payMethod: "现金", cash: 20m);

        Assert.Equal("现金", (await Reload(id)).PayMethod);
        Assert.Empty(PaymentRows(id));
    }

    [Fact]
    public async Task GetDetailAsync_MixedPayment_ReturnsComposition()
    {
        SetStock(ProductAId, 10, 5.00m);
        var r = await MixedSaleAsync(2, Pay("现金", 12m), Pay("微信", 8m));
        var id = GetResultDataProp<int>(r.Data!, "id");

        var d = await SaleSvc.GetDetailAsync(id);
        Assert.Equal(0, d.Code);
        var pays = Prop<IEnumerable<object>>(d.Data, "payments")!.ToList();
        Assert.Equal(2, pays.Count);
        var wechat = pays.First(x => Prop<string>(x, "payMethod") == "微信");
        Assert.Equal(8.00m, Prop<decimal>(wechat, "amount"));
    }
}
