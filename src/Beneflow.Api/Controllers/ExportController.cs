using System.Text;
using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Beneflow.Api.Controllers;

/// <summary>
/// 通用导出：基于 ClosedXML 生成美观 .xlsx，或同结构 UTF-8 BOM 的 CSV。
/// 数据范围 = 各页面当前筛选条件下的全量（不分页）。
/// 所有端点支持 ?format=csv 切换为 CSV 输出，默认 xlsx。
/// </summary>
[Route("api/v1/export")]
public class ExportController : BaseApiController
{
    private readonly IExcelExportService _excel;
    private readonly IProductService _products;
    private readonly IStockService _stocks;
    private readonly ISaleService _sales;
    private readonly IReportService _reports;

    public ExportController(IExcelExportService excel, IProductService products, IStockService stocks, ISaleService sales, IReportService reports)
    { _excel = excel; _products = products; _stocks = stocks; _sales = sales; _reports = reports; }

    // ---- 销售单列表 ----
    [HttpGet("sales")]
    public async Task<IActionResult> Sales([FromQuery] string? keyword, [FromQuery] string? dateFrom,
        [FromQuery] string? dateTo, [FromQuery] string? payMethod, [FromQuery] string? format)
    {
        var rows = await _sales.ExportListAsync(keyword, dateFrom, dateTo, payMethod);
        return Output(new ExcelReport
        {
            SheetName = "销售单列表", Title = "销售单列表", FileName = "销售单列表.xlsx",
            Columns = new()
            {
                f("orderNo","单号"), m("totalAmount","总金额"), m("discountAmount","折扣"),
                m("payAmount","实收"), f("payMethod","收款方式"),
                m("cashAmount","现金"), m("changeAmount","找零"),
                f("status","状态"), f("isCredit","赊账"), f("wechatId","微信号"),
                dt("createdAt","时间"), f("createdByName","操作人"),
            },
            Rows = rows, SummaryLabel = "合计",
            SummaryFields = { "totalAmount", "discountAmount", "payAmount", "cashAmount", "changeAmount" },
        }, format);
    }

    // ---- 商品列表 ----
    [HttpGet("products")]
    public async Task<IActionResult> Products([FromQuery] string? keyword, [FromQuery] string? format)
    {
        var rows = await _products.ExportListAsync(keyword);
        return Output(new ExcelReport
        {
            SheetName = "商品列表", Title = "商品列表", FileName = "商品列表.xlsx",
            Columns = new()
            {
                f("barcode","条码"), f("name","名称"), f("categoryName","分类"),
                f("unit","单位"), f("spec","规格"), m("salePrice","售价"), m("costPrice","成本"),
                n("stockQuantity","库存"), d("expireDate","有效期"), f("status","状态"),
            },
            Rows = rows,
        }, format);
    }

    // ---- 实时库存 ----
    [HttpGet("inventory")]
    public async Task<IActionResult> Inventory([FromQuery] string? keyword, [FromQuery] string? status, [FromQuery] string? format)
    {
        var rows = await _stocks.ExportInventoryAsync(keyword, status);
        return Output(new ExcelReport
        {
            SheetName = "实时库存", Title = "实时库存", FileName = "实时库存.xlsx",
            Columns = new()
            {
                f("barcode","条码"), f("name","名称"), f("categoryName","分类"),
                m("costPrice","成本"), m("salePrice","售价"), n("stockQuantity","库存"),
                m("stockAmount","库存金额"), n("safetyStock","安全库存"),
                d("expireDate","有效期"), f("status","状态"),
            },
            Rows = rows, SummaryLabel = "合计", SummaryFields = { "stockAmount" },
        }, format);
    }

    // ---- 库存流水 ----
    [HttpGet("stock-logs")]
    public async Task<IActionResult> StockLogs([FromQuery] string? keyword, [FromQuery] string? changeType, [FromQuery] string? format)
    {
        var rows = await _stocks.ExportLogAsync(keyword, changeType);
        return Output(new ExcelReport
        {
            SheetName = "库存流水", Title = "库存流水", FileName = "库存流水.xlsx",
            Columns = new()
            {
                dt("createdAt","时间"), f("productName","商品"), f("barcode","条码"),
                f("changeType","类型"), n("changeQty","变动数量"),
                n("beforeQty","变动前"), n("afterQty","变动后"),
                f("refNo","关联单号"), f("createdByName","操作人"),
            },
            Rows = rows,
        }, format);
    }

    // ---- 赊账记录 ----
    [HttpGet("credits")]
    public async Task<IActionResult> Credits([FromQuery] string? keyword, [FromQuery] string? status,
        [FromQuery] string? dateFrom, [FromQuery] string? dateTo, [FromQuery] string? format)
    {
        var rows = await _sales.ExportCreditAsync(keyword, status, dateFrom, dateTo);
        return Output(new ExcelReport
        {
            SheetName = "赊账记录", Title = "赊账记录", FileName = "赊账记录.xlsx",
            Columns = new()
            {
                f("wechatId","微信号"), f("phone","手机号"), f("saleOrderNo","单号"),
                m("creditAmount","欠款"), m("paidAmount","已还"), m("remainingAmount","剩余"),
                f("status","状态"), f("remark","备注"), dt("createdAt","赊账时间"),
            },
            Rows = rows, SummaryLabel = "合计",
            SummaryFields = { "creditAmount", "paidAmount", "remainingAmount" },
        }, format);
    }

    // ---- 库存预警 / 采购建议 ----
    [HttpGet("stock-warnings")]
    public async Task<IActionResult> StockWarnings([FromQuery] string? format)
    {
        var raw = await _stocks.WarningsAsync();
        var rows = raw.Select(o =>
        {
            var d = Props(o);
            // 建议补货 = max(安全库存*2 - 当前库存, 安全库存)，与前端 StockWarnings.html 一致
            var qty = Convert.ToInt32(d.GetOrDefault("stockQuantity"));
            var safe = Convert.ToInt32(d.GetOrDefault("safetyStock"));
            d["suggested"] = Math.Max(safe * 2 - qty, safe);
            return d;
        }).ToList();
        return Output(new ExcelReport
        {
            SheetName = "采购建议", Title = "库存预警 / 采购建议", FileName = "采购建议.xlsx",
            Columns = new()
            {
                f("barcode","条码"), f("name","名称"), n("stockQuantity","当前库存"),
                n("safetyStock","安全库存"), n("shortage","缺口"), n("suggested","建议补货"),
                f("status","状态"),
            },
            Rows = rows,
        }, format);
    }

    // ---- 日销售 Top5 ----
    [HttpGet("daily-sales")]
    public async Task<IActionResult> DailySales([FromQuery] string? date, [FromQuery] string? format)
    {
        var d = DateTime.TryParse(date, out var parsed) ? parsed.Date : DateTime.Today;
        var obj = await _reports.DailySalesAsync(d);
        var top5 = obj?.GetType().GetProperty("top5")?.GetValue(obj) as System.Collections.IEnumerable
                   ?? Array.Empty<object>();
        var rows = top5.Cast<object>().Select(Props).ToList();
        return Output(new ExcelReport
        {
            SheetName = "日销售Top5",
            Title = "日销售 Top5（" + d.ToString("yyyy-MM-dd") + "）",
            FileName = "日销售Top5_" + d.ToString("yyyyMMdd") + ".xlsx",
            Columns = new()
            {
                f("name","商品"), n("qty","销量"), m("amount","金额"),
            },
            Rows = rows,
        }, format);
    }

    // ---- 供应商对账单 ----
    [HttpGet("supplier-statement")]
    public async Task<IActionResult> SupplierStatement([FromQuery] int supplierId,
        [FromQuery] string? dateFrom, [FromQuery] string? dateTo, [FromQuery] string? format)
    {
        var obj = await _reports.SupplierStatementAsync(supplierId, dateFrom, dateTo);
        var items = obj?.GetType().GetProperty("items")?.GetValue(obj) as System.Collections.IEnumerable
                    ?? Array.Empty<object>();
        var rows = items.Cast<object>().Select(Props).ToList();
        // 合计行：ExcelExportService 按 SummaryFields 自动累加 totalAmount
        return Output(new ExcelReport
        {
            SheetName = "供应商对账单",
            Title = "供应商对账单",
            FileName = "供应商对账单_" + supplierId + ".xlsx",
            Columns = new()
            {
                f("orderNo","单号"), dt("createdAt","时间"),
                n("totalQty","数量"), m("totalAmount","金额"),
            },
            Rows = rows, SummaryLabel = "合计", SummaryFields = { "totalAmount" },
        }, format);
    }

    // ---- helpers ----
    private IActionResult Output(ExcelReport report, string? format)
    {
        if (format == "csv")
        {
            var csv = _excel.BuildCsv(report);
            var bytes = Encoding.UTF8.GetBytes(csv);
            var csvName = Path.GetFileNameWithoutExtension(report.FileName) + ".csv";
            return File(bytes, "text/csv; charset=utf-8", csvName);
        }
        var xlsx = _excel.Build(report);
        return File(xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", report.FileName);
    }

    private static ExcelColumn f(string field, string title) => new() { Field = field, Title = title };
    private static ExcelColumn m(string field, string title) => new() { Field = field, Title = title, Type = ExcelColumnType.Money };
    private static ExcelColumn n(string field, string title) => new() { Field = field, Title = title, Type = ExcelColumnType.Number };
    private static ExcelColumn d(string field, string title) => new() { Field = field, Title = title, Type = ExcelColumnType.Date };
    private static ExcelColumn dt(string field, string title) => new() { Field = field, Title = title, Type = ExcelColumnType.DateTime };

    /// <summary>反射匿名对象 → 字典（key=属性名，value=属性值）</summary>
    private static Dictionary<string, object?> Props(object? obj)
    {
        var dict = new Dictionary<string, object?>();
        if (obj == null) return dict;
        foreach (var p in obj.GetType().GetProperties())
            dict[p.Name] = p.GetValue(obj);
        return dict;
    }
}

internal static class DictExtensions
{
    public static object? GetOrDefault(this Dictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) ? v : null;
}
