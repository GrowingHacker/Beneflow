using Beneflow.Api.Models.Entities;

namespace Beneflow.Api.Models;

// ==================== 请求 DTO ====================

public class LoginDto
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class ChangePasswordDto
{
    public string OldPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}

public class SaleItemDto
{
    public int ProductId { get; set; }
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal SubTotal { get; set; }
}

/// <summary>创建销售单请求（收银台结算）</summary>
public class CreateSaleDto
{
    public List<SaleItemDto> Items { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal PayAmount { get; set; }
    public string PayMethod { get; set; } = "现金";
    public decimal CashAmount { get; set; }
    public decimal ChangeAmount { get; set; }
    public bool IsCredit { get; set; }
    public string? WechatId { get; set; }
    public string? Phone { get; set; }
    public string? Remark { get; set; }
}

public class PurchaseDetailDto
{
    public int ProductId { get; set; }
    public string? Name { get; set; }
    /// <summary>进货数量</summary>
    public decimal Qty { get; set; }
    /// <summary>进价（成本）</summary>
    public decimal CostPrice { get; set; }
    /// <summary>生产日期（可选，临期管理用）</summary>
    public DateTime? ProduceDate { get; set; }
}

/// <summary>创建进货单请求</summary>
public class CreatePurchaseDto
{
    public int SupplierId { get; set; }
    public List<PurchaseDetailDto> Details { get; set; } = new();
    public decimal TotalQty { get; set; }
    public decimal TotalAmount { get; set; }
    public string? Remark { get; set; }
}

public class PurchaseReturnItemDto
{
    public int ProductId { get; set; }
    public decimal Qty { get; set; }
    public decimal CostPrice { get; set; }
}

/// <summary>创建采购退货单请求</summary>
public class CreatePurchaseReturnDto
{
    public int SupplierId { get; set; }
    public List<PurchaseReturnItemDto> Items { get; set; } = new();
    public decimal ReturnTotal { get; set; }
    public string? Reason { get; set; }
}

public class SaleReturnItemDto
{
    /// <summary>销售明细 ID（优先匹配）</summary>
    public long? DetailId { get; set; }
    public string? Name { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>创建销售退货单请求</summary>
public class CreateSaleReturnDto
{
    public string OriginalOrderNo { get; set; } = "";
    public List<SaleReturnItemDto> Items { get; set; } = new();
    public string RefundMethod { get; set; } = "原路退回";
    public decimal RefundTotal { get; set; }
}

public class StockCheckItemDto
{
    public int Id { get; set; }
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal BookQty { get; set; }
    public decimal ActualQty { get; set; }
}

/// <summary>创建盘点单请求</summary>
public class CreateStockCheckDto
{
    public string Range { get; set; } = "全部商品";
    public List<StockCheckItemDto> Items { get; set; } = new();
    public string Status { get; set; } = "草稿";
}

public class SettleCreditDto
{
    /// <summary>还款金额；为 0 或不传则全额结清（兼容旧版调用）</summary>
    public decimal PayAmount { get; set; }
    public string PayMethod { get; set; } = "微信";
}

public class UpdateCreditDto
{
    public string? Phone { get; set; }
    public string? Remark { get; set; }
}

public class ProductUpsertDto
{
    public int Id { get; set; }
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public int CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string Unit { get; set; } = "";
    public string? Spec { get; set; }
    public decimal SalePrice { get; set; }
    public decimal CostPrice { get; set; }
    public decimal StockQuantity { get; set; }
    public decimal SafetyStock { get; set; }
    public bool HasExpiry { get; set; }
    public int ShelfLifeDays { get; set; }

    /// <summary>
    /// 原始提交值（JSON 可能是字符串 "上架"/"下架" 或 bool true/false）。
    /// 业务代码请用 <see cref="StatusBool"/> 取解析后的布尔值。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public object? StatusRaw { get; set; } = true;

    /// <summary>解析后的布尔状态（true=上架 / false=下架）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Status => StatusRaw switch
    {
        true => true,
        false => false,
        "上架" or "启用" or "1" => true,
        string s when !string.IsNullOrEmpty(s) => bool.TryParse(s, out var b) && b,
        _ => true
    };

    public string? Remark { get; set; }
}

public class SupplierUpsertDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Contact { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Remark { get; set; }

    /// <summary>原始提交值（字符串 "启用"/"停用" 或 bool true/false）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public object? StatusRaw { get; set; } = true;

    /// <summary>解析后的布尔状态（true=启用 / false=停用）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Status => StatusRaw switch
    {
        true => true,
        false => false,
        "启用" or "上架" or "1" => true,
        string s when !string.IsNullOrEmpty(s) => bool.TryParse(s, out var b) && b,
        _ => true
    };
}

public class UserCreateDto
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public int RoleId { get; set; }
    public string? Remark { get; set; }
}

public class UserUpdateDto
{
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public int RoleId { get; set; }
    public string? Remark { get; set; }
    public string? Password { get; set; }
}

public class RoleSetPermsDto
{
    public List<int> MenuIds { get; set; } = new();
}
