namespace Beneflow.Api.Models.Entities;

/// <summary>商品分类表（最多三级）</summary>
public class ProductCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public int Sort { get; set; }
    public bool Status { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>商品表（移动加权平均成本核算）</summary>
public class Product
{
    public int Id { get; set; }
    /// <summary>条码，唯一；可空时由店内自动生成店内码</summary>
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    /// <summary>单位：瓶/包/罐…</summary>
    [System.ComponentModel.DataAnnotations.StringLength(10)]
    public string Unit { get; set; } = "";
    public string? Spec { get; set; }
    /// <summary>销售价</summary>
    public decimal SalePrice { get; set; }
    /// <summary>成本价（移动加权平均）</summary>
    public decimal CostPrice { get; set; }
    /// <summary>当前库存，支持小数称重件</summary>
    public decimal StockQuantity { get; set; }
    /// <summary>安全库存（低于触发补货提醒）</summary>
    public decimal SafetyStock { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(200)]
    public string? ImageUrl { get; set; }
    /// <summary>是否有效期管理</summary>
    public bool HasExpiry { get; set; }
    /// <summary>保质期天数</summary>
    public int ShelfLifeDays { get; set; }
    /// <summary>是否称重商品（散装称重，按斤/公斤计价，无条码）</summary>
    public bool IsWeighted { get; set; }
    /// <summary>拼音码（名称拼音首字母，用于收银快速搜索）</summary>
    [System.ComponentModel.DataAnnotations.StringLength(20)]
    public string PinyinCode { get; set; } = "";
    /// <summary>状态：true=上架 / false=下架</summary>
    public bool Status { get; set; } = true;
    public string? Remark { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public ProductCategory Category { get; set; } = null!;
}

/// <summary>商品批次表（有效期管理；进货按批次入库）</summary>
public class ProductBatch
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public string BatchNo { get; set; } = "";
    public DateTime? ProduceDate { get; set; }
    /// <summary>有效期至</summary>
    public DateTime ExpireDate { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>临期处理标记</summary>
    public bool IsProcessed { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Product Product { get; set; } = null!;
}

/// <summary>供应商表</summary>
public class Supplier
{
    public int Id { get; set; }
    /// <summary>名称唯一</summary>
    public string Name { get; set; } = "";
    public string? Contact { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Remark { get; set; }
    /// <summary>状态：true=启用 / false=停用</summary>
    public bool Status { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
