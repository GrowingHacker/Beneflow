namespace Beneflow.Api.Models.Entities;

/// <summary>库存流水表（所有库存变动的唯一凭据）</summary>
public class StockLog
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    /// <summary>变动类型：期初建账/采购入库/采购退货出库/销售出库/销售退货入库/盘点调整</summary>
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string ChangeType { get; set; } = "";
    /// <summary>变动数量（正负）</summary>
    public decimal ChangeQty { get; set; }
    public decimal BeforeQty { get; set; }
    public decimal AfterQty { get; set; }
    /// <summary>关联单号</summary>
    [System.ComponentModel.DataAnnotations.StringLength(30)]
    public string RefNo { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Product Product { get; set; } = null!;
}

/// <summary>盘点单主表</summary>
public class StockCheck
{
    public int Id { get; set; }
    /// <summary>盘点单号，唯一：SC + 日期 + 序号</summary>
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string OrderNo { get; set; } = "";
    /// <summary>盘点范围：全部商品/仅临期商品…</summary>
    public string Range { get; set; } = "全部商品";
    /// <summary>状态：false=草稿 / true=已确认</summary>
    public bool Status { get; set; }
    public int ItemCount { get; set; }
    public decimal ProfitQty { get; set; }
    public decimal LossQty { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ConfirmedAt { get; set; }
}

/// <summary>盘点单明细</summary>
public class StockCheckDetail
{
    public long Id { get; set; }
    public int CheckId { get; set; }
    public int ProductId { get; set; }
    public decimal BookQty { get; set; }
    public decimal ActualQty { get; set; }
    public decimal DiffQty { get; set; }
}
