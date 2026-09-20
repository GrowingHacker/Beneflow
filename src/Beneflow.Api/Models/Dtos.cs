using System.ComponentModel.DataAnnotations;
using Beneflow.Api.Models.Entities;

namespace Beneflow.Api.Models;

// ==================== 请求 DTO ====================
//
// 校验职责划分（避免同一个规则散落两处）：
//   · 本文件的数据注解只管「字段形态」——必填、长度上限、ID 是否为合法正数，
//     这些约束与数据库列定义一一对应，注解不通过会直接返回 400，不再进入 Service。
//   · 业务规则（库存够不够、是否允许赊账、数量是否必须为整数……）一律留在 Service，
//     因为它们需要查库或依赖系统设置，注解表达不了。
//
// 长度上限的来源是 Data/Configurations/ 下的列定义；两者必须保持一致，
// 否则超长字符串会在 SaveChanges 时抛截断异常（500）而不是给出友好提示。

public class LoginDto
{
    /// <summary>登录用户名</summary>
    [Required(ErrorMessage = "请输入用户名")]
    [StringLength(20, ErrorMessage = "用户名长度不能超过 20 位")]
    public string Username { get; set; } = "";

    /// <summary>登录密码（明文提交，服务端按盐哈希比对）</summary>
    [Required(ErrorMessage = "请输入密码")]
    public string Password { get; set; } = "";
}

public class ChangePasswordDto
{
    /// <summary>原密码</summary>
    [Required(ErrorMessage = "请输入原密码")]
    public string OldPassword { get; set; } = "";

    /// <summary>新密码（≥6 位且含数字和字母）</summary>
    [Required(ErrorMessage = "请输入新密码")]
    public string NewPassword { get; set; } = "";
}

public class SaleItemDto
{
    /// <summary>商品 ID</summary>
    [Range(1, int.MaxValue, ErrorMessage = "商品 ID 无效")]
    public int ProductId { get; set; }

    /// <summary>条码（快照，用于对账）</summary>
    [StringLength(20, ErrorMessage = "条码长度不能超过 20 位")]
    public string Barcode { get; set; } = "";

    /// <summary>商品名称（快照）</summary>
    [StringLength(100, ErrorMessage = "商品名称长度不能超过 100 位")]
    public string Name { get; set; } = "";

    /// <summary>销售数量（支持三位小数，称重商品用）</summary>
    public decimal Qty { get; set; }
    /// <summary>成交单价</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>小计 = Qty × UnitPrice</summary>
    public decimal SubTotal { get; set; }
}

/// <summary>创建销售单请求（收银台结算）</summary>
public class CreateSaleDto
{
    /// <summary>销售明细行</summary>
    public List<SaleItemDto> Items { get; set; } = new();
    /// <summary>商品总额（前端展示值，后端重算）</summary>
    public decimal TotalAmount { get; set; }
    /// <summary>优惠金额（整单折扣让利）</summary>
    public decimal DiscountAmount { get; set; }
    /// <summary>
    /// 折扣率（单位「折」：8.8 表示 8.8 折 = 88%）。可选，仅「按折扣」录入时传。
    /// 有值时后端以折率重算优惠金额（折率可独立校验，金额不可），忽略 DiscountAmount；单位是「折」不是「%」。
    /// </summary>
    public decimal? DiscountRate { get; set; }
    /// <summary>抹零金额（现金取整让利，非现金单必须为 0）</summary>
    public decimal RoundOffAmount { get; set; }
    /// <summary>应收金额（前端展示值，后端重算）</summary>
    public decimal PayAmount { get; set; }

    /// <summary>收款方式：现金/微信/支付宝/银行/赊账</summary>
    [StringLength(20, ErrorMessage = "收款方式长度不能超过 20 位")]
    public string PayMethod { get; set; } = "现金";

    /// <summary>收款额（递钞额）：现金单顾客实际交出的钱，用于算找零；非现金传 0</summary>
    public decimal CashAmount { get; set; }
    /// <summary>找零金额（前端展示值，后端重算）</summary>
    public decimal ChangeAmount { get; set; }
    /// <summary>是否赊账</summary>
    public bool IsCredit { get; set; }

    /// <summary>赊账客户微信号</summary>
    [StringLength(50, ErrorMessage = "微信号长度不能超过 50 位")]
    public string? WechatId { get; set; }

    /// <summary>赊账客户手机号</summary>
    [StringLength(20, ErrorMessage = "手机号长度不能超过 20 位")]
    public string? Phone { get; set; }

    /// <summary>备注</summary>
    public string? Remark { get; set; }
}

public class PurchaseDetailDto
{
    /// <summary>商品 ID</summary>
    [Range(1, int.MaxValue, ErrorMessage = "商品 ID 无效")]
    public int ProductId { get; set; }

    /// <summary>商品名称</summary>
    [StringLength(100, ErrorMessage = "商品名称长度不能超过 100 位")]
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
    /// <summary>供应商 ID</summary>
    [Range(1, int.MaxValue, ErrorMessage = "请选择供应商")]
    public int SupplierId { get; set; }

    /// <summary>进货明细行</summary>
    public List<PurchaseDetailDto> Details { get; set; } = new();
    /// <summary>总数量（由明细汇总）</summary>
    public decimal TotalQty { get; set; }
    /// <summary>总金额（由明细汇总）</summary>
    public decimal TotalAmount { get; set; }
    /// <summary>备注</summary>
    public string? Remark { get; set; }
}

public class PurchaseReturnItemDto
{
    /// <summary>商品 ID</summary>
    [Range(1, int.MaxValue, ErrorMessage = "商品 ID 无效")]
    public int ProductId { get; set; }

    /// <summary>退货数量</summary>
    public decimal Qty { get; set; }
    /// <summary>退货单价（成本价）</summary>
    public decimal CostPrice { get; set; }
}

/// <summary>创建采购退货单请求</summary>
public class CreatePurchaseReturnDto
{
    /// <summary>供应商 ID</summary>
    [Range(1, int.MaxValue, ErrorMessage = "请选择供应商")]
    public int SupplierId { get; set; }

    /// <summary>退货明细行</summary>
    public List<PurchaseReturnItemDto> Items { get; set; } = new();
    /// <summary>退货总额</summary>
    public decimal ReturnTotal { get; set; }
    /// <summary>退货原因</summary>
    public string? Reason { get; set; }
}

public class SaleReturnItemDto
{
    /// <summary>销售明细 ID（优先匹配）</summary>
    public long? DetailId { get; set; }

    /// <summary>商品名称（快照）</summary>
    [StringLength(100, ErrorMessage = "商品名称长度不能超过 100 位")]
    public string? Name { get; set; }

    /// <summary>退货数量</summary>
    public decimal Qty { get; set; }
    /// <summary>退货单价</summary>
    public decimal UnitPrice { get; set; }
}

/// <summary>创建销售退货单请求</summary>
public class CreateSaleReturnDto
{
    /// <summary>原销售单号</summary>
    [StringLength(20, ErrorMessage = "销售单号长度不能超过 20 位")]
    public string OriginalOrderNo { get; set; } = "";

    /// <summary>退货明细行</summary>
    public List<SaleReturnItemDto> Items { get; set; } = new();

    /// <summary>退款方式</summary>
    [StringLength(20, ErrorMessage = "退款方式长度不能超过 20 位")]
    public string RefundMethod { get; set; } = "原路退回";

    /// <summary>退款总额</summary>
    public decimal RefundTotal { get; set; }
}

public class StockCheckItemDto
{
    /// <summary>商品 ID</summary>
    public int Id { get; set; }

    /// <summary>条码</summary>
    [StringLength(20, ErrorMessage = "条码长度不能超过 20 位")]
    public string Barcode { get; set; } = "";

    /// <summary>商品名称</summary>
    [StringLength(100, ErrorMessage = "商品名称长度不能超过 100 位")]
    public string Name { get; set; } = "";

    /// <summary>账面库存</summary>
    public decimal BookQty { get; set; }
    /// <summary>实盘库存</summary>
    public decimal ActualQty { get; set; }
}

/// <summary>创建盘点单请求</summary>
public class CreateStockCheckDto
{
    /// <summary>盘点范围（全部/分类/关键词）</summary>
    [StringLength(50, ErrorMessage = "盘点范围长度不能超过 50 位")]
    public string Range { get; set; } = "全部商品";

    /// <summary>盘点明细行</summary>
    public List<StockCheckItemDto> Items { get; set; } = new();

    /// <summary>提交状态：草稿 或 已确认</summary>
    [StringLength(10, ErrorMessage = "状态长度不能超过 10 位")]
    public string Status { get; set; } = "草稿";
}

public class SettleCreditDto
{
    /// <summary>还款金额；为 0 或不传则全额结清（兼容旧版调用）</summary>
    public decimal PayAmount { get; set; }

    /// <summary>还款方式</summary>
    [StringLength(20, ErrorMessage = "还款方式长度不能超过 20 位")]
    public string PayMethod { get; set; } = "微信";
}

public class UpdateCreditDto
{
    /// <summary>手机号</summary>
    [StringLength(20, ErrorMessage = "手机号长度不能超过 20 位")]
    public string? Phone { get; set; }

    /// <summary>备注</summary>
    public string? Remark { get; set; }
}

public class ProductUpsertDto
{
    /// <summary>商品 ID（新增时传 0）</summary>
    public int Id { get; set; }

    /// <summary>条码（留空自动生成）</summary>
    [StringLength(20, ErrorMessage = "条码长度不能超过 20 位")]
    public string Barcode { get; set; } = "";

    /// <summary>商品名称</summary>
    [StringLength(100, ErrorMessage = "商品名称长度不能超过 100 位")]
    public string Name { get; set; } = "";

    /// <summary>分类 ID</summary>
    public int CategoryId { get; set; }

    /// <summary>分类名称（仅回显用）</summary>
    [StringLength(100, ErrorMessage = "分类名称长度不能超过 100 位")]
    public string? CategoryName { get; set; }

    /// <summary>单位（瓶/盒/斤等）</summary>
    [StringLength(10, ErrorMessage = "单位长度不能超过 10 位")]
    public string Unit { get; set; } = "";

    /// <summary>规格</summary>
    [StringLength(50, ErrorMessage = "规格长度不能超过 50 位")]
    public string? Spec { get; set; }

    /// <summary>售价</summary>
    public decimal SalePrice { get; set; }
    /// <summary>成本价</summary>
    public decimal CostPrice { get; set; }

    /// <summary>优惠方式：""=无优惠 / "特价" / "折扣"（优惠方案只在档案里定）</summary>
    [StringLength(10, ErrorMessage = "优惠方式长度不能超过 10 位")]
    public string PromoType { get; set; } = "";
    /// <summary>促销价（PromoType="特价" 时生效）</summary>
    public decimal PromoPrice { get; set; }
    /// <summary>促销折率（PromoType="折扣" 时生效，单位「折」：8.8 = 8.8 折）</summary>
    public decimal PromoRate { get; set; }
    /// <summary>优惠启用开关</summary>
    public bool PromoEnabled { get; set; } = true;
    /// <summary>优惠开始日期（可空 = 不限）</summary>
    public DateTime? PromoStartAt { get; set; }
    /// <summary>优惠结束日期（可空 = 不限，含结束日当天）</summary>
    public DateTime? PromoEndAt { get; set; }
    /// <summary>库存数量</summary>
    public decimal StockQuantity { get; set; }
    /// <summary>安全库存（低于则预警）</summary>
    public decimal SafetyStock { get; set; }
    /// <summary>是否启用有效期管理</summary>
    public bool HasExpiry { get; set; }
    /// <summary>保质期天数（HasExpiry=true 时生效）</summary>
    public int ShelfLifeDays { get; set; }
    /// <summary>是否称重商品（散装称重，按斤/公斤计价）</summary>
    public bool IsWeighted { get; set; }

    /// <summary>
    /// 原始提交值（JSON 可能是字符串 "上架"/"下架" 或 bool true/false）。
    /// 业务代码请用 <see cref="Status"/> 取解析后的布尔值。
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

    /// <summary>备注</summary>
    public string? Remark { get; set; }
}

/// <summary>
/// 商品导入的一行数据（预览页解析出来后原样回传，服务端会重新校验一次）。
/// 字段与导入模板一一对应；<see cref="Row"/> 只用于把失败信息定位回 Excel 行号。
/// </summary>
public class ProductImportRowDto
{
    /// <summary>原 Excel 行号（从 2 开始，1 是表头）</summary>
    public int Row { get; set; }

    /// <summary>条码；留空表示称重/散装商品，落库时自动生成店内码</summary>
    [StringLength(20, ErrorMessage = "条码长度不能超过 20 位")]
    public string Barcode { get; set; } = "";

    /// <summary>商品名称</summary>
    [StringLength(100, ErrorMessage = "商品名称长度不能超过 100 位")]
    public string Name { get; set; } = "";

    /// <summary>分类名称；系统里不存在时自动创建，留空归入「未分类」</summary>
    [StringLength(50, ErrorMessage = "分类名称长度不能超过 50 位")]
    public string? CategoryName { get; set; }

    /// <summary>单位（瓶/袋/斤等）</summary>
    [StringLength(10, ErrorMessage = "单位长度不能超过 10 位")]
    public string Unit { get; set; } = "";

    /// <summary>规格</summary>
    [StringLength(50, ErrorMessage = "规格长度不能超过 50 位")]
    public string? Spec { get; set; }

    /// <summary>售价</summary>
    public decimal SalePrice { get; set; }
    /// <summary>成本价</summary>
    public decimal CostPrice { get; set; }
    /// <summary>库存；只在新建商品时作为期初库存写入</summary>
    public decimal StockQuantity { get; set; }
    /// <summary>安全库存</summary>
    public decimal SafetyStock { get; set; }
    /// <summary>保质期天数；&gt; 0 即启用有效期管理</summary>
    public int ShelfLifeDays { get; set; }
    /// <summary>是否称重商品</summary>
    public bool IsWeighted { get; set; }
    /// <summary>状态：true=上架 / false=下架</summary>
    public bool Status { get; set; } = true;
    /// <summary>备注</summary>
    public string? Remark { get; set; }
}

/// <summary>批量导入商品档案请求</summary>
public class ProductImportDto
{
    /// <summary>待导入的商品行</summary>
    public List<ProductImportRowDto> Rows { get; set; } = new();

    /// <summary>条码已存在时的处理：skip=跳过 / update=覆盖更新（覆盖不改动库存）</summary>
    [StringLength(10, ErrorMessage = "处理方式长度不能超过 10 位")]
    public string DuplicatePolicy { get; set; } = "skip";
}

public class SupplierUpsertDto
{
    /// <summary>供应商 ID（新增时传 0）</summary>
    public int Id { get; set; }

    /// <summary>供应商名称</summary>
    [StringLength(100, ErrorMessage = "供应商名称长度不能超过 100 位")]
    public string Name { get; set; } = "";

    /// <summary>联系人</summary>
    [StringLength(50, ErrorMessage = "联系人长度不能超过 50 位")]
    public string? Contact { get; set; }

    /// <summary>联系电话</summary>
    [StringLength(20, ErrorMessage = "手机号长度不能超过 20 位")]
    public string? Phone { get; set; }

    /// <summary>地址</summary>
    public string? Address { get; set; }
    /// <summary>备注</summary>
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
    /// <summary>登录用户名</summary>
    [Required(ErrorMessage = "请输入用户名")]
    [StringLength(20, ErrorMessage = "用户名长度不能超过 20 位")]
    public string Username { get; set; } = "";

    /// <summary>初始密码（≥6 位且含数字和字母）</summary>
    [Required(ErrorMessage = "请输入密码")]
    public string Password { get; set; } = "";

    /// <summary>姓名</summary>
    [Required(ErrorMessage = "请输入姓名")]
    public string Name { get; set; } = "";

    /// <summary>手机号</summary>
    [StringLength(20, ErrorMessage = "手机号长度不能超过 20 位")]
    public string? Phone { get; set; }

    /// <summary>角色 ID</summary>
    public int RoleId { get; set; }
    /// <summary>备注</summary>
    public string? Remark { get; set; }
}

public class UserUpdateDto
{
    /// <summary>姓名</summary>
    [Required(ErrorMessage = "请输入姓名")]
    public string Name { get; set; } = "";

    /// <summary>手机号</summary>
    [StringLength(20, ErrorMessage = "手机号长度不能超过 20 位")]
    public string? Phone { get; set; }

    /// <summary>角色 ID</summary>
    public int RoleId { get; set; }
    /// <summary>备注</summary>
    public string? Remark { get; set; }
    /// <summary>新密码（留空表示不修改）</summary>
    public string? Password { get; set; }
}

public class RoleSetPermsDto
{
    /// <summary>该角色可访问的菜单 ID 集合</summary>
    public List<int> MenuIds { get; set; } = new();
}

/// <summary>演示数据状态：前端据此决定是否常驻提示「当前数据为演示数据」</summary>
public class DemoDataStatusDto
{
    /// <summary>true=仍是演示数据（从未执行过初始化）；false=已初始化，可正式使用</summary>
    public bool IsDemoData { get; set; }
}

/// <summary>初始化数据请求：confirm 必须原样填写「清空」，服务端强制校验</summary>
public class ClearDemoDataDto
{
    /// <summary>确认文字，必须为「清空」</summary>
    [Required(ErrorMessage = "请输入确认文字")]
    [StringLength(10, ErrorMessage = "确认文字长度不能超过 10 位")]
    public string Confirm { get; set; } = "";
}

/// <summary>初始化数据结果：清空前自动备份的落点（备份失败不阻断清空，原因随之返回）</summary>
public class InitializeDataResultDto
{
    /// <summary>清空前自动备份的 .bak 完整路径；未做备份时为 null</summary>
    public string? BackupPath { get; set; }

    /// <summary>备份失败原因；备份成功或跳过备份时为 null</summary>
    public string? BackupError { get; set; }
}
