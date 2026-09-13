using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>采购管理（部分）：进货单 Excel 导入解析 + 导入模板生成。</summary>
public partial class PurchaseService : IPurchaseService
{
    // ================= Excel 导入 =================

    /// <summary>
    /// 解析进货单导入 Excel：每个 Sheet 名称必须与供应商名称一致（一个 Sheet = 一张进货单）。
    /// 表头按名称匹配列（条码/商品名称/数量/进价/生产日期），按条码优先匹配商品，条码为空时按名称匹配。
    /// 名称不匹配供应商的 Sheet 会列入 skippedSheets 跳过（如「使用说明」Sheet）。不写库。
    /// </summary>
    public async Task<ApiResult<object>> ParseImportAsync(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return ApiResult<object>.Fail("请选择 Excel 文件");
        var ext = Path.GetExtension(file.FileName).ToLower();
        if (ext != ".xlsx" && ext != ".xls")
            return ApiResult<object>.Fail("仅支持 .xlsx / .xls 格式");

        // 保存上传文件到专用目录，并清理过期文件（>2 小时）
        var uploadDir = Path.Combine(AppContext.BaseDirectory, "uploads", "purchases");
        Directory.CreateDirectory(uploadDir);
        CleanupOldUploads(uploadDir, TimeSpan.FromHours(2));
        var savedPath = Path.Combine(uploadDir, $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}");
        await using (var fs = File.Create(savedPath))
            await file.CopyToAsync(fs);

        using var wb = new XLWorkbook(savedPath);

        // 加载全部供应商，按名称匹配 Sheet 名
        var suppliers = await _db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Name);

        var orders = new List<object>();
        var unmatchedSheets = new List<string>();  // Sheet 名未匹配到供应商（可让用户创建）
        var unmatchedProducts = new List<object>(); // 商品未匹配到（可让用户创建）
        var skippedSheets = new List<string>();     // 匹配到供应商但格式/数据有问题
        var totalRows = 0;

        foreach (var ws in wb.Worksheets)
        {
            var sheetName = ws.Name.Trim();
            // 跳过模板说明页等非数据 Sheet
            if (string.Equals(sheetName, "使用说明", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!suppliers.TryGetValue(sheetName, out var supplier))
            {
                unmatchedSheets.Add(sheetName);
                continue;
            }

            // 解析表头
            var headerMap = new Dictionary<string, int>();
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            for (var c = 1; c <= lastCol; c++)
            {
                var h = ws.Cell(1, c).GetString().Trim();
                if (!string.IsNullOrEmpty(h)) headerMap[h] = c;
            }
            var barcodeCol = FindCol(headerMap, "条码", "barcode", "条形码");
            var nameCol = FindCol(headerMap, "商品名称", "名称", "品名", "name");
            var specCol = FindCol(headerMap, "规格", "spec");
            var categoryCol = FindCol(headerMap, "分类", "类别", "category");
            var qtyCol = FindCol(headerMap, "数量", "qty");
            var priceCol = FindCol(headerMap, "进价", "成本价", "价格", "costPrice");
            var dateCol = FindCol(headerMap, "生产日期", "日期", "produceDate");

            if (barcodeCol == 0 && nameCol == 0)
            {
                skippedSheets.Add(sheetName + "（缺少条码/名称列）");
                continue;
            }
            if (qtyCol == 0 || priceCol == 0)
            {
                skippedSheets.Add(sheetName + "（缺少数量/进价列）");
                continue;
            }

            // 读取数据行
            var rawRows = new List<(int row, string barcode, string name, string spec, string category, decimal qty, decimal price, DateTime? date)>();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            for (var r = 2; r <= lastRow; r++)
            {
                var barcode = barcodeCol > 0 ? ws.Cell(r, barcodeCol).GetString().Trim() : "";
                var name = nameCol > 0 ? ws.Cell(r, nameCol).GetString().Trim() : "";
                if (string.IsNullOrEmpty(barcode) && string.IsNullOrEmpty(name)) continue;
                var spec = specCol > 0 ? ws.Cell(r, specCol).GetString().Trim() : "";
                var category = categoryCol > 0 ? ws.Cell(r, categoryCol).GetString().Trim() : "";
                var qty = GetDecimal(ws.Cell(r, qtyCol));
                var price = priceCol > 0 ? GetDecimal(ws.Cell(r, priceCol)) : 0;
                var date = dateCol > 0 ? GetDate(ws.Cell(r, dateCol)) : null;
                rawRows.Add((r, barcode, name, spec, category, qty, price, date));
            }

            if (rawRows.Count == 0)
            {
                skippedSheets.Add(sheetName + "（无数据行）");
                continue;
            }
            totalRows += rawRows.Count;

            // 批量匹配商品：先按条码，未命中再按名称
            var allBarcodes = rawRows.Where(x => !string.IsNullOrEmpty(x.barcode)).Select(x => x.barcode).Distinct().ToList();
            var byBarcode = allBarcodes.Count > 0
                ? await _db.Products.AsNoTracking().Where(p => allBarcodes.Contains(p.Barcode))
                    .ToDictionaryAsync(p => p.Barcode)
                : new Dictionary<string, Product>();
            var unmatchedNames = rawRows
                .Where(x => !string.IsNullOrEmpty(x.barcode) && !byBarcode.ContainsKey(x.barcode) && !string.IsNullOrEmpty(x.name))
                .Select(x => x.name).Distinct().ToList();
            var noBarcodeNames = rawRows
                .Where(x => string.IsNullOrEmpty(x.barcode) && !string.IsNullOrEmpty(x.name))
                .Select(x => x.name).Distinct().ToList();
            var allNames = unmatchedNames.Concat(noBarcodeNames).Distinct().ToList();
            var byName = allNames.Count > 0
                ? await _db.Products.AsNoTracking().Where(p => allNames.Contains(p.Name))
                    .ToDictionaryAsync(p => p.Name)
                : new Dictionary<string, Product>();

            // 构建预览明细
            var items = new List<object>();
            var errors = new List<object>();
            decimal totalQty = 0, totalAmount = 0;
            foreach (var (rowNum, barcode, name, spec, category, qty, price, date) in rawRows)
            {
                Product? p = null;
                if (!string.IsNullOrEmpty(barcode) && byBarcode.TryGetValue(barcode, out var pb))
                    p = pb;
                else if (!string.IsNullOrEmpty(name) && byName.TryGetValue(name, out var pn))
                    p = pn;

                if (p == null)
                {
                    // 未匹配到商品，收集到独立列表让前端弹窗创建
                    unmatchedProducts.Add(new { sheetName, row = rowNum, barcode, name, spec, category, qty, price });
                    continue;
                }
                if (qty <= 0)
                {
                    errors.Add(new { row = rowNum, barcode, name = p.Name, message = "数量必须大于 0" });
                    continue;
                }
                if (price < 0)
                {
                    errors.Add(new { row = rowNum, barcode, name = p.Name, message = "进价不能为负" });
                    continue;
                }

                totalQty += qty;
                totalAmount += qty * price;
                items.Add(new
                {
                    productId = p.Id, barcode = p.Barcode, name = p.Name,
                    isWeighted = p.IsWeighted, unit = p.Unit,
                    qty, costPrice = price, produceDate = date?.ToString("yyyy-MM-dd"),
                });
            }

            orders.Add(new
            {
                sheetName, supplierId = supplier.Id, supplierName = supplier.Name,
                items, errors, rowCount = rawRows.Count,
                totalQty = Math.Round(totalQty, 3), totalAmount = Math.Round(totalAmount, 2),
            });
        }

        if (orders.Count == 0 && unmatchedSheets.Count == 0 && unmatchedProducts.Count == 0)
            return ApiResult<object>.Fail("未解析到有效进货单：请确认每个 Sheet 名称与供应商名称一致，并包含商品明细");

        return ApiResult<object>.Ok(new { orders, unmatchedSheets, unmatchedProducts, skippedSheets, totalSheets = wb.Worksheets.Count, totalRows });
    }

    /// <summary>批量创建进货单：逐张调用 CreateAsync（各自独立事务），返回成功/失败明细</summary>
    public async Task<ApiResult<object>> CreateBatchAsync(List<CreatePurchaseDto> dtos)
    {
        if (dtos == null || dtos.Count == 0)
            return ApiResult<object>.Fail("没有进货单数据");

        var created = new List<object>();
        var failed = new List<object>();
        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            try
            {
                var r = await CreateAsync(dto);
                if (r.Code == 0 && r.Data != null) created.Add(r.Data);
                else failed.Add(new { index = i, supplierId = dto.SupplierId, message = r.Message });
            }
            catch (Exception ex)
            {
                failed.Add(new { index = i, supplierId = dto.SupplierId, message = ex.Message });
            }
        }
        return ApiResult<object>.Ok(new { created, failed, total = dtos.Count });
    }

    /// <summary>生成进货单批量导入模板 .xlsx：第1个 Sheet 为使用说明，其后每个示例 Sheet 名 = 供应商名</summary>
    public byte[] BuildImportTemplate()
    {
        using var wb = new XLWorkbook();

        // ---- Sheet 1：使用说明 ----
        var guide = wb.Worksheets.Add("使用说明");
        guide.Cell(1, 1).Value = "进货单批量导入说明";
        guide.Cell(1, 1).Style.Font.Bold = true;
        guide.Cell(1, 1).Style.Font.FontSize = 14;
        guide.Range(1, 1, 1, 5).Merge();
        guide.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var lines = new[]
        {
            "1. 每个 Sheet 对应一张进货单，Sheet 名称必须与系统中的供应商名称完全一致。",
            "2. 复制示例供应商 Sheet 后，将 Sheet 标签改名为实际供应商名称即可。",
            "3. 删除示例数据行，按表头填写实际商品明细。",
            "4. 「条码」必填且需与系统商品条码一致；称重商品无条码时留空，但需填「商品名称」。",
            "5. 「数量」必填大于 0；「进价」必填不能为负；「生产日期」选填，格式 YYYY-MM-DD。",
            "6. 本「使用说明」Sheet 不会参与导入，可保留。"
        };
        for (var i = 0; i < lines.Length; i++)
        {
            guide.Cell(i + 2, 1).Value = lines[i];
            guide.Cell(i + 2, 1).Style.Font.FontColor = XLColor.FromHtml("#606266");
            guide.Range(i + 2, 1, i + 2, 5).Merge();
            guide.Cell(i + 2, 1).Style.Alignment.WrapText = true;
        }
        guide.Column(1).Width = 80;
        guide.Row(1).Height = 28;

        // ---- Sheet 2：示例供应商（复制后改名）----
        AddSupplierSheet(wb, "示例供应商（复制后改名）");

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void AddSupplierSheet(XLWorkbook wb, string sheetName)
    {
        var ws = wb.Worksheets.Add(sheetName);
        var headers = new[] { "条码", "商品名称", "规格", "分类", "数量", "进价", "生产日期" };
        for (var i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Font.FontColor = XLColor.White;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#409eff");
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            c.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            c.Style.Border.OutsideBorderColor = XLColor.FromHtml("#C0C4CC");
        }
        var sample = new[] { "6901234567890", "示例商品（请删除此行）", "500g", "零食", "10", "5.50", "2026-09-01" };
        for (var i = 0; i < sample.Length; i++)
        {
            var c = ws.Cell(2, i + 1);
            c.Value = sample[i];
            c.Style.Font.FontColor = XLColor.FromHtml("#C0C4CC");
            c.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            c.Style.Border.OutsideBorderColor = XLColor.FromHtml("#C0C4CC");
        }
        ws.Column(1).Width = 20;
        ws.Column(2).Width = 28;
        ws.Column(3).Width = 12;
        ws.Column(4).Width = 12;
        ws.Column(5).Width = 10;
        ws.Column(6).Width = 10;
        ws.Column(7).Width = 14;
        ws.SheetView.FreezeRows(1);
    }

    // ---- Excel 单元格读取辅助 ----
    private static int FindCol(Dictionary<string, int> map, params string[] names)
    {
        foreach (var n in names)
            if (map.TryGetValue(n, out var col)) return col;
        return 0;
    }
    private static decimal GetDecimal(IXLCell cell)
    {
        if (cell.TryGetValue<decimal>(out var d)) return d;
        if (decimal.TryParse(cell.GetString().Trim(), out var v)) return v;
        return 0;
    }
    private static DateTime? GetDate(IXLCell cell)
    {
        if (cell.TryGetValue<DateTime>(out var dt)) return dt;
        if (DateTime.TryParse(cell.GetString().Trim(), out var d)) return d;
        return null;
    }

    /// <summary>清理上传目录中超过指定时长的残留文件</summary>
    private static void CleanupOldUploads(string dir, TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.Now - maxAge;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* 清理失败不影响导入 */ }
    }
}
