namespace Beneflow.Api.Models.Entities;

/// <summary>进货单主表（采购入库）</summary>
public class PurchaseOrder
{
    public int Id { get; set; }
    /// <summary>进货单号，唯一：PO + 日期 + 序号</summary>
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string OrderNo { get; set; } = "";
    public int SupplierId { get; set; }
    public decimal TotalQty { get; set; }
    public decimal TotalAmount { get; set; }
    public string? Remark { get; set; }
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
    [System.ComponentModel.DataAnnotations.StringLength(20)]
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

/// <summary>销售单主表</summary>
public class SaleOrder
{
    public int Id { get; set; }
    /// <summary>销售单号，唯一：SO + 日期 + 序号</summary>
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string OrderNo { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    /// <summary>实收金额</summary>
    public decimal PayAmount { get; set; }
    /// <summary>收款方式：现金/微信/支付宝/赊账</summary>
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string PayMethod { get; set; } = "现金";
    public decimal CashAmount { get; set; }
    public decimal ChangeAmount { get; set; }
    public bool IsCredit { get; set; }
    /// <summary>赊账顾客微信号（欠款标识）</summary>
    [System.ComponentModel.DataAnnotations.StringLength(50)]
    public string? WechatId { get; set; }
    public string? Remark { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>销售单明细（成本价为销售时快照，用于毛利核算）</summary>
public class SaleOrderDetail
{
    public long Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    /// <summary>商品名称快照（退货页按名称回带）</summary>
    public string ProductName { get; set; } = "";
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string Barcode { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
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
    [System.ComponentModel.DataAnnotations.StringLength(20)]
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
    [System.ComponentModel.DataAnnotations.StringLength(50)]
    public string WechatId { get; set; } = "";
    [System.ComponentModel.DataAnnotations.StringLength(20)]
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
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string PayMethod { get; set; } = "微信";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
