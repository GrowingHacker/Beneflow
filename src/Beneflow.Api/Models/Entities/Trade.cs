namespace Beneflow.Api.Models.Entities;

/// <summary>进货单主表（采购入库）</summary>
public class PurchaseOrder
{
    public int Id { get; set; }
    /// <summary>进货单号，唯一：PO + 日期 + 序号</summary>
    public string OrderNo { get; set; } = "";
    public int SupplierId { get; set; }
    public decimal TotalQty { get; set; }
    public decimal TotalAmount { get; set; }
    public string? Remark { get; set; }
    public bool IsVoided { get; set; }
    public DateTime? VoidedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Supplier Supplier { get; set; } = null!;
}

/// <summary>进货单明细</summary>
public class PurchaseOrderDetail
{
    public long Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public decimal Qty { get; set; }
    /// <summary>进价</summary>
    public decimal CostPrice { get; set; }
    public decimal SubTotal { get; set; }

    public Product Product { get; set; } = null!;
}

/// <summary>采购退货单主表（退给供应商，出库）</summary>
public class PurchaseReturn
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public int SupplierId { get; set; }
    /// <summary>应退款项金额</summary>
    public decimal RefundAmount { get; set; }
    public string? Reason { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Supplier Supplier { get; set; } = null!;
}

/// <summary>采购退货单明细</summary>
public class PurchaseReturnDetail
{
    public long Id { get; set; }
    public int ReturnId { get; set; }
    public int ProductId { get; set; }
    public decimal Qty { get; set; }
    public decimal CostPrice { get; set; }
    public decimal SubTotal { get; set; }
}

/// <summary>
/// 销售单主表。金额字段按零售行业惯例成链（商品总额 → 优惠 → 抹零），恒满足：
/// 商品总额 TotalAmount = Σ(明细挂牌价快照 × 数量)，即**未优惠前**的原价合计（小票上的「商品总额」）；
/// 优惠金额 DiscountAmount = 档案促销让利 + 整单优惠，其中促销让利 = 原价合计 − Σ 明细成交小计；
/// 折后金额 = TotalAmount − DiscountAmount（＝成交合计 − 整单优惠）；
/// 应收金额 = 折后金额 − RoundOffAmount = PayAmount；
/// 收款额 CashAmount（现金单为顾客递出金额，非现金为 0）；
/// 找零 ChangeAmount = CashAmount − PayAmount（仅现金单）；
/// 实收金额 ReceivedAmount = 实际收到的净额：现金/微信/支付宝 = PayAmount；
/// 赊账 = 累计已还款额（开单记 0，每笔还款累加；退货抵欠款不算「收到」，不计入）。
/// 整单优惠有两种录入方式，落库以 DiscountAmount（金额）为准：
/// ① 按折率录入（DiscountRate 有值，单位「折」，8.80 = 8.8 折）⇒ 先算折后金额再减法反算优惠额；
/// ② 按金额录入（DiscountRate 为 null）。
/// 之所以以金额为准、折率只作录入来源与审计痕迹：金额可加总（报表合计/毛利/对账），折率不可加总。
/// </summary>
public class SaleOrder
{
    public int Id { get; set; }
    /// <summary>销售单号，唯一：SO + 日期 + 序号</summary>
    public string OrderNo { get; set; } = "";
    /// <summary>商品总额：Σ(挂牌价 × 数量)，未优惠前的原价合计</summary>
    public decimal TotalAmount { get; set; }
    /// <summary>优惠金额：档案促销让利 + 整单优惠（权威值，两种录入方式都归到这里）</summary>
    public decimal DiscountAmount { get; set; }
    /// <summary>折扣率（单位「折」，8.80 = 8.8 折 = 88%）：仅按折率录入时有值；按金额录入时为 null，故可区分单据的让利来源</summary>
    public decimal? DiscountRate { get; set; }
    /// <summary>抹零金额：现金收款时的取整让利（行业惯例仅现金抹零，非现金为 0）</summary>
    public decimal RoundOffAmount { get; set; }
    /// <summary>应收金额：商品总额 − 优惠金额 − 抹零金额，即顾客应付</summary>
    public decimal PayAmount { get; set; }
    /// <summary>实收金额：实际收到的净额。非赊账 = 应收金额；赊账 = 累计已还款额（开单 0，每笔还款累加）</summary>
    public decimal ReceivedAmount { get; set; }
    /// <summary>收款方式：现金/微信/支付宝/赊账</summary>
    public string PayMethod { get; set; } = "现金";
    /// <summary>收款额（递钞额）：现金单为顾客实际交出的钱，非现金为 0</summary>
    public decimal CashAmount { get; set; }
    /// <summary>找零金额：收款额 − 应收金额，仅现金单有值</summary>
    public decimal ChangeAmount { get; set; }
    public bool IsCredit { get; set; }
    /// <summary>赊账顾客微信号（欠款标识）</summary>
    public string? WechatId { get; set; }
    public string? Remark { get; set; }
    /// <summary>作废标记：true 表示该订单已作废（不入销售额/订单数/毛利统计）</summary>
    public bool IsVoided { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 销售单明细（成本价为销售时快照，用于毛利核算）。
/// 单价口径：<see cref="OriginalPrice"/> 为挂牌价快照、<see cref="UnitPrice"/> 为成交单价快照（已含档案优惠）。
/// 商品总额按成交单价合计（<see cref="SubTotal"/>），退货退款也按成交单价计（数量 × 成交单价），
/// 所以成交单价必须落库快照、不能事后按档案重算——档案里的优惠方案随时会改。
/// </summary>
public class SaleOrderDetail
{
    public long Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    /// <summary>商品名称快照（退货页按名称回带）</summary>
    public string ProductName { get; set; } = "";
    public string Barcode { get; set; } = "";
    public decimal Quantity { get; set; }
    /// <summary>成交单价快照（已含档案优惠；退货退款的计价基数）</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>挂牌价快照（无优惠时与成交单价相同）：档案优惠方案日后会改，历史单据靠它才能解释「当时让了多少」</summary>
    public decimal OriginalPrice { get; set; }
    /// <summary>成本价快照</summary>
    public decimal CostPrice { get; set; }
    public decimal SubTotal { get; set; }
    /// <summary>已退货数量（支持多次部分退货）</summary>
    public decimal ReturnedQuantity { get; set; }

    public SaleOrder Order { get; set; } = null!;
}

/// <summary>销售退货单主表（顾客退款，入库回补）</summary>
public class SaleReturn
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public int SaleOrderId { get; set; }
    /// <summary>退款方式：原路退回/现金/微信/支付宝</summary>
    public string RefundMethod { get; set; } = "原路退回";
    public decimal RefundAmount { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public SaleOrder SaleOrder { get; set; } = null!;
}

/// <summary>销售退货单明细</summary>
public class SaleReturnDetail
{
    public long Id { get; set; }
    public int ReturnId { get; set; }
    public long SaleOrderDetailId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal SubTotal { get; set; }
}

/// <summary>赊账记录表（以顾客微信号作为欠款标识）</summary>
public class CreditSale
{
    public int Id { get; set; }
    public int SaleOrderId { get; set; }
    public string WechatId { get; set; } = "";
    public string? Phone { get; set; }
    public string? Remark { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RemainingAmount { get; set; }
    /// <summary>状态：true=已结清 / false=未结清</summary>
    public bool Status { get; set; }
    public DateTime? SettledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public SaleOrder SaleOrder { get; set; } = null!;
}

/// <summary>赊账还款记录表</summary>
public class CreditPayment
{
    public int Id { get; set; }
    public int CreditSaleId { get; set; }
    public decimal PayAmount { get; set; }
    /// <summary>还款方式：微信/现金/支付宝</summary>
    public string PayMethod { get; set; } = "微信";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
