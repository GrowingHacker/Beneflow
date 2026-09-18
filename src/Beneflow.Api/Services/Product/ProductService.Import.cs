using System.Text.Json;
using System.Text.RegularExpressions;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>
/// 商品档案（部分）：Excel 批量导入的解析、落库与模板生成。
/// 与进货单导入的区别：进货单的模板由本系统规定，列名可枚举；
/// 商品导入面对的是用户从旧软件/旧账本导出的文件，列名五花八门，
/// 所以这里多做了一层「列映射」——先按同义词自动识别，识别不准可由前端覆盖后重新解析。
/// </summary>
public partial class ProductService : IProductService
{
    /// <summary>单次导入允许的最大数据行数，超出直接报错，避免一次请求撑爆响应与事务</summary>
    private const int MaxImportRows = 5000;

    /// <summary>导入支持的字段：key 供前端做列映射，aliases 供表头自动识别</summary>
    private sealed record ImportFieldDef(string Key, string Label, bool Required, string[] Aliases);

    private static readonly ImportFieldDef[] ImportFieldDefs =
    {
        new("barcode",       "条码",       false, new[] { "条码", "条形码", "商品条码", "国际条码", "barcode" }),
        new("name",          "商品名称",   true,  new[] { "商品名称", "名称", "品名", "货品名称", "商品", "name" }),
        new("category",      "分类",       false, new[] { "分类", "类别", "商品分类", "类目", "category" }),
        new("unit",          "单位",       false, new[] { "单位", "基本单位", "计量单位", "unit" }),
        new("spec",          "规格",       false, new[] { "规格", "规格型号", "包装规格", "spec" }),
        new("salePrice",     "售价",       false, new[] { "售价", "零售价", "销售价", "卖价", "单价", "salePrice" }),
        new("costPrice",     "成本价",     false, new[] { "成本价", "进价", "成本", "采购价", "costPrice" }),
        new("stock",         "库存",       false, new[] { "库存", "库存数量", "期初库存", "现有库存", "结存", "stock" }),
        new("safetyStock",   "安全库存",   false, new[] { "安全库存", "库存下限", "预警库存", "safetyStock" }),
        new("shelfLifeDays", "保质期天数", false, new[] { "保质期天数", "保质期", "有效期天数", "shelfLifeDays" }),
        new("isWeighted",    "是否称重",   false, new[] { "是否称重", "称重", "散装", "称重商品", "isWeighted" }),
        new("status",        "状态",       false, new[] { "状态", "上架状态", "商品状态", "status" }),
        new("remark",        "备注",       false, new[] { "备注", "说明", "remark" }),
    };

    // ================= Excel 批量导入：解析预览 =================

    /// <summary>
    /// 解析商品档案导入 Excel（只解析、不写库）。
    /// 表头先按同义词自动识别；显式传入的 mapping（字段 key → 表头文字）优先，用于前端修正识别错的列。
    /// 每个数据行都带校验结果与「新增 / 已存在」判定，前端确认后才调用 <see cref="ImportBatchAsync"/> 落库。
    /// </summary>
    public async Task<ApiResult<object>> ParseImportAsync(IFormFile file, string? mappingJson)
    {
        if (file == null || file.Length == 0)
            return ApiResult<object>.Fail("请选择 Excel 文件");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xls")
            return ApiResult<object>.Fail("仅支持 .xlsx / .xls 格式");

        // 落盘到专用目录并清理过期文件（>2 小时），与进货单导入保持一致
        var uploadDir = Path.Combine(AppContext.BaseDirectory, "uploads", "products");
        Directory.CreateDirectory(uploadDir);
        CleanupOldUploads(uploadDir, TimeSpan.FromHours(2));
        var savedPath = Path.Combine(uploadDir, $"{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}");
        await using (var fs = File.Create(savedPath))
            await file.CopyToAsync(fs);

        // ClosedXML 只能读 OpenXML（.xlsx/.xlsm）。旧版 .xls（BIFF）、被改名成 .xlsx 的 CSV、
        // 损坏文件都会在这里抛异常——必须本地兜住并给可操作的中文提示，
        // 否则会冒到全局兜底，用户看到的是 500「服务器内部错误」。
        var wb = ExcelUtil.TryOpen(savedPath);
        if (wb is null)
            return ApiResult<object>.Fail("无法读取该文件：请确认是有效的 .xlsx；若为旧版 .xls，请先用 Excel 另存为 .xlsx 再导入");
        using var wbScope = wb;   // 提前返回的分支较多，交给方法退出时统一释放
        var ws = PickDataSheet(wb);
        if (ws == null)
            return ApiResult<object>.Fail("未找到可导入的工作表：请确认文件第一行是表头、第二行起是商品数据");

        // ---- 读表头 ----
        var (headers, headerCols) = ReadHeaders(ws);

        // ---- 列映射：显式指定优先，其余自动识别 ----
        var explicitMap = ParseMappingJson(mappingJson);
        var mapping = new Dictionary<string, string>();
        foreach (var f in ImportFieldDefs)
        {
            if (explicitMap.TryGetValue(f.Key, out var want))
            {
                if (string.IsNullOrWhiteSpace(want)) continue;   // 用户显式清空 ⇒ 这一列不要，也不再做自动识别
                var h = NormalizeHeader(want!);
                if (headerCols.ContainsKey(h)) { mapping[f.Key] = h; continue; }
                // 指定的表头在当前文件里找不到（多半是换过文件了），退回自动识别
            }
            var auto = MatchColumn(headerCols.Keys, f.Aliases);
            if (auto != null) mapping[f.Key] = auto;
        }
        var colOf = mapping.ToDictionary(kv => kv.Key, kv => headerCols[kv.Value]);

        // 没有「商品名称」列说明列映射没对，返回表头让前端弹映射区让用户手动指定
        if (!colOf.ContainsKey("name"))
        {
            return ApiResult<object>.Ok(new
            {
                sheetName = ws.Name, headers,
                fields = ImportFieldDefs.Select(f => new { key = f.Key, label = f.Label, required = f.Required }),
                mapping = ImportFieldDefs.ToDictionary(f => f.Key, f => mapping.TryGetValue(f.Key, out var v) ? v : null),
                needMapping = true, rows = new List<object>(),
                summary = new { totalRows = 0, importable = 0, newCount = 0, existingCount = 0, errorCount = 0, newCategories = Array.Empty<string>() },
                message = "未能自动识别「商品名称」列，请在列映射中手动指定后点「重新解析」",
            });
        }

        // ---- 读数据行（整行空白的忽略，常见于表格尾部空行）----
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var rawRows = new List<ImportRow>();
        for (var r = 2; r <= lastRow; r++)
        {
            var row = ReadImportRow(ws, r, colOf);
            if (row != null) rawRows.Add(row);
        }
        if (rawRows.Count == 0)
            return ApiResult<object>.Fail("表格中没有数据行，请确认第二行起为商品数据");
        if (rawRows.Count > MaxImportRows)
            return ApiResult<object>.Fail($"单次最多导入 {MaxImportRows} 行，当前文件有 {rawRows.Count} 行，请拆分后重试");

        // ---- 条码占用情况 ----
        // 条码唯一索引带过滤条件（Barcode <> ''）但**不过滤软删除行**，被删除的商品仍占着条码，
        // 所以这里不能只看未删除商品，否则新增时会撞唯一索引报 500。
        var barcodes = rawRows.Where(x => x.Barcode.Length > 0).Select(x => x.Barcode).Distinct().ToList();
        var activeBarcodes = new HashSet<string>();
        var deletedBarcodes = new HashSet<string>();
        if (barcodes.Count > 0)
        {
            var taken = await _db.Products.AsNoTracking()
                .Where(p => barcodes.Contains(p.Barcode))
                .Select(p => new { p.Barcode, p.IsDeleted }).ToListAsync();
            foreach (var t in taken)
                (t.IsDeleted ? deletedBarcodes : activeBarcodes).Add(t.Barcode);
        }

        var catNames = await _db.Categories.AsNoTracking().Select(c => c.Name).ToListAsync();
        var knownCats = new HashSet<string>(catNames, StringComparer.Ordinal);

        var seenBarcode = new Dictionary<string, int>();   // 条码 → 首次出现行号，用于揪出文件内重复
        var newCategories = new SortedSet<string>(StringComparer.Ordinal);
        var rows = new List<object>();
        int errorCount = 0, newCount = 0, existingCount = 0;

        foreach (var x in rawRows)
        {
            var errors = ValidateRow(x);
            var isExisting = false;

            if (x.Barcode.Length > 0)
            {
                if (seenBarcode.TryGetValue(x.Barcode, out var firstRow))
                    errors.Add($"条码在文件中重复（第 {firstRow} 行已出现）");
                else
                {
                    seenBarcode[x.Barcode] = x.Row;
                    if (deletedBarcodes.Contains(x.Barcode))
                        errors.Add("该条码被已删除的商品占用，请改用其他条码或先恢复该商品");
                    else if (activeBarcodes.Contains(x.Barcode))
                        isExisting = true;
                }
            }

            if (errors.Count > 0) errorCount++;
            else if (isExisting) existingCount++;
            else newCount++;

            // 只有确定会新建的行才算「将新建的分类」
            if (errors.Count == 0 && !isExisting)
            {
                var cat = string.IsNullOrWhiteSpace(x.CategoryName) ? "未分类" : x.CategoryName.Trim();
                if (!knownCats.Contains(cat)) newCategories.Add(cat);
            }

            rows.Add(new
            {
                row = x.Row, barcode = x.Barcode, name = x.Name,
                categoryName = x.CategoryName ?? "", unit = x.Unit, spec = x.Spec ?? "",
                salePrice = x.SalePrice, costPrice = x.CostPrice,
                stockQuantity = x.Stock, safetyStock = x.SafetyStock,
                shelfLifeDays = x.ShelfLifeDays, isWeighted = x.IsWeighted,
                status = x.Status ? "上架" : "下架", remark = x.Remark ?? "",
                isExisting, messages = errors,
            });
        }

        return ApiResult<object>.Ok(new
        {
            sheetName = ws.Name,
            headers,
            fields = ImportFieldDefs.Select(f => new { key = f.Key, label = f.Label, required = f.Required }),
            // 每个字段都给一个值（未识别到为 null），前端可以直接双向绑定列映射下拉框
            mapping = ImportFieldDefs.ToDictionary(f => f.Key, f => mapping.TryGetValue(f.Key, out var v) ? v : null),
            needMapping = false,
            rows,
            summary = new
            {
                totalRows = rawRows.Count, importable = newCount + existingCount,
                newCount, existingCount, errorCount, newCategories = newCategories.ToList(),
            },
        });
    }

    // ================= Excel 批量导入：落库 =================

    /// <summary>
    /// 商品档案导入落库：整批一个事务，要么全成功要么全回滚，避免出现「导到一半」的中间态。
    /// duplicatePolicy = skip / update 决定条码已存在时的行为；update 只覆盖档案字段、**不改动库存**
    /// （库存变动必须留下流水，导入不应绕过这条规则）。
    /// </summary>
    public async Task<ApiResult<object>> ImportBatchAsync(ProductImportDto dto)
    {
        var rows = dto?.Rows ?? new List<ProductImportRowDto>();
        if (rows.Count == 0) return ApiResult<object>.Fail("没有可导入的商品数据");
        var updateExisting = string.Equals(dto!.DuplicatePolicy, "update", StringComparison.OrdinalIgnoreCase);

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 分类：按名称取现有 ID，不存在的顺手建（与单个新增商品的行为保持一致）
            var categories = await _db.Categories.ToDictionaryAsync(c => c.Name);
            var createdCategories = new List<string>();
            ProductCategory ResolveCategory(string? categoryName)
            {
                var name = string.IsNullOrWhiteSpace(categoryName) ? "未分类" : categoryName.Trim();
                if (name.Length > 50) name = name[..50];
                if (categories.TryGetValue(name, out var hit)) return hit;
                var fresh = new ProductCategory { Name = name };
                _db.Categories.Add(fresh);
                categories[name] = fresh;
                createdCategories.Add(name);
                return fresh;
            }

            var barcodes = rows.Where(r => !string.IsNullOrWhiteSpace(r.Barcode)).Select(r => r.Barcode.Trim()).Distinct().ToList();
            var existing = barcodes.Count > 0
                ? await _db.Products.Where(p => barcodes.Contains(p.Barcode)).ToDictionaryAsync(p => p.Barcode)
                : new Dictionary<string, Product>();

            var created = new List<Product>();
            var usedBarcodes = new HashSet<string>(existing.Keys);   // 防止同一批内重号
            var failed = new List<object>();
            int updated = 0, skipped = 0;

            // 无条码商品的店内码生成：既避开本批已用条码，也照 `CreateAsync` 的做法查一次库
            // （条码唯一索引不过滤软删除行，所以这里同样不能按 IsDeleted 过滤）
            async Task<string> NewInStoreBarcodeAsync()
            {
                while (true)
                {
                    var code = "L" + DateTime.Now.ToString("yyMMddHHmmssfff") + Random.Shared.Next(10, 99);
                    if (!usedBarcodes.Contains(code) && !await _db.Products.AnyAsync(p => p.Barcode == code))
                        return code;
                }
            }

            foreach (var r in rows)
            {
                var name = r.Name?.Trim() ?? "";
                if (name.Length == 0)
                {
                    failed.Add(new { row = r.Row, name, message = "商品名称为空" });
                    continue;
                }
                var barcode = r.Barcode?.Trim() ?? "";

                // 条码已存在：按策略跳过或覆盖（覆盖不改库存）
                if (barcode.Length > 0 && existing.TryGetValue(barcode, out var old))
                {
                    if (!updateExisting) { skipped++; continue; }
                    ApplyImportFields(old, r, ResolveCategory(r.CategoryName));
                    old.UpdatedAt = DateTime.Now;
                    updated++;
                    continue;
                }

                var p = new Product { Barcode = barcode, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
                // 新分类还没有 ID，靠导航属性在 SaveChanges 时补外键
                ApplyImportFields(p, r, ResolveCategory(r.CategoryName));
                p.StockQuantity = Math.Max(0, r.StockQuantity);   // 库存只在新建时作为期初建账写入
                if (barcode.Length == 0) p.Barcode = await NewInStoreBarcodeAsync();
                usedBarcodes.Add(p.Barcode);
                _db.Products.Add(p);
                created.Add(p);
            }

            // 先保存拿到自增 ID，批次号与库存流水都要用
            await _db.SaveChangesAsync();

            foreach (var p in created)
            {
                if (p.StockQuantity <= 0) continue;
                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id, ChangeType = "期初建账", ChangeQty = p.StockQuantity,
                    BeforeQty = 0, AfterQty = p.StockQuantity, RefNo = "IMPORT",
                    CreatedBy = _me.Id, CreatedAt = DateTime.Now,
                });
                if (p.HasExpiry && p.ShelfLifeDays > 0)
                {
                    _db.Batches.Add(new ProductBatch
                    {
                        ProductId = p.Id, BatchNo = $"B{DateTime.Now:yyMMdd}{p.Id:D3}",
                        ProduceDate = DateTime.Today,
                        ExpireDate = DateTime.Today.AddDays(p.ShelfLifeDays),
                        Quantity = p.StockQuantity,
                    });
                }
            }
            await _db.SaveChangesAsync();

            await _logs.WriteAsync("商品管理", "批量导入商品",
                $"新增 {created.Count} 条、更新 {updated} 条、跳过 {skipped} 条、失败 {failed.Count} 条");
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            return ApiResult<object>.Ok(new
            {
                created = created.Count, updated, skipped, failed,
                total = rows.Count, newCategories = createdCategories,
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("商品导入失败：" + ex.Message, ex);
        }
    }

    // ================= 导入模板 =================

    /// <summary>生成商品档案导入模板 .xlsx：第 1 个 Sheet 是使用说明，第 2 个是带示例数据的表头</summary>
    public byte[] BuildImportTemplate()
    {
        using var wb = new XLWorkbook();

        // ---- Sheet 1：使用说明 ----
        var guide = wb.Worksheets.Add("使用说明");
        guide.Cell(1, 1).Value = "商品档案批量导入说明";
        guide.Cell(1, 1).Style.Font.Bold = true;
        guide.Cell(1, 1).Style.Font.FontSize = 14;
        guide.Range(1, 1, 1, 6).Merge();
        guide.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        var lines = new[]
        {
            "1. 在「商品档案」Sheet 的表头下逐行填写商品，一行一个商品；表头行请勿删除或改名。",
            "2. 只有「商品名称」是必填；其余列留空即按默认值处理（单位按「件」、价格与库存按 0）。",
            "3. 「条码」留空时系统会自动生成店内码，适用于散装称重商品；已存在的条码可选择跳过或覆盖更新。",
            "4. 「分类」填名称即可，系统里没有的分类会自动创建；整列留空则统一归入「未分类」。",
            "5. 「库存」只在新建商品时作为期初库存写入并生成「期初建账」流水；覆盖更新已有商品时不会改动库存。",
            "6. 「是否称重」填 是/否；「保质期天数」填写大于 0 的数字即自动启用有效期管理。",
            "7. 也可以直接导入你自己的表格：系统会按同义词识别列名，识别不准可在预览页手动指定列映射。",
            "8. 本「使用说明」Sheet 不参与导入，可以保留。",
        };
        for (var i = 0; i < lines.Length; i++)
        {
            guide.Cell(i + 2, 1).Value = lines[i];
            guide.Cell(i + 2, 1).Style.Font.FontColor = XLColor.FromHtml("#606266");
            guide.Range(i + 2, 1, i + 2, 6).Merge();
            guide.Cell(i + 2, 1).Style.Alignment.WrapText = true;
        }
        guide.Column(1).Width = 96;
        guide.Row(1).Height = 28;

        // ---- Sheet 2：商品档案 ----
        var ws = wb.Worksheets.Add("商品档案");
        var headers = new[] { "条码", "商品名称", "分类", "单位", "规格", "售价", "成本价", "库存", "安全库存", "保质期天数", "是否称重", "状态", "备注" };
        var widths = new[] { 22, 26, 12, 8, 12, 10, 10, 10, 12, 14, 12, 10, 20 };
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
            ws.Column(i + 1).Width = widths[i];
        }

        var samples = new[]
        {
            new[] { "6901234567890", "示例商品·矿泉水（请删除此行）", "饮料", "瓶", "550ml", "2.00", "1.20", "48", "12", "", "否", "上架", "" },
            new[] { "", "示例商品·散装花生（请删除此行）", "零食", "斤", "", "12.80", "8.50", "20", "5", "", "是", "上架", "无条码自动生成店内码" },
        };
        for (var r = 0; r < samples.Length; r++)
        {
            for (var i = 0; i < samples[r].Length; i++)
            {
                var c = ws.Cell(r + 2, i + 1);
                c.Value = samples[r][i];
                c.Style.Font.FontColor = XLColor.FromHtml("#C0C4CC");
                c.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                c.Style.Border.OutsideBorderColor = XLColor.FromHtml("#C0C4CC");
            }
        }
        ws.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ================= 解析辅助 =================

    /// <summary>导入行的原始值（已按列映射取好，尚未校验）</summary>
    private sealed record ImportRow(int Row, string Barcode, string Name, string? CategoryName, string Unit,
        string? Spec, decimal SalePrice, decimal CostPrice, decimal Stock, decimal SafetyStock,
        int ShelfLifeDays, bool IsWeighted, bool Status, string? Remark);

    /// <summary>逐字段校验一行；返回空列表表示这一行可以导入</summary>
    private static List<string> ValidateRow(ImportRow x)
    {
        var errors = new List<string>();
        if (x.Name.Length == 0) errors.Add("商品名称为空");
        else if (x.Name.Length > 100) errors.Add("商品名称超过 100 字");
        if (x.Barcode.Length > 20) errors.Add("条码超过 20 位");
        if (x.Unit.Length > 10) errors.Add("单位超过 10 字");
        if (x.Spec is { Length: > 50 }) errors.Add("规格超过 50 字");
        if (x.CategoryName is { Length: > 50 }) errors.Add("分类名称超过 50 字");
        if (x.SalePrice < 0) errors.Add("售价不能为负");
        if (x.CostPrice < 0) errors.Add("成本价不能为负");
        if (x.Stock < 0) errors.Add("库存不能为负");
        if (x.SafetyStock < 0) errors.Add("安全库存不能为负");
        if (x.ShelfLifeDays < 0) errors.Add("保质期天数不能为负");
        return errors;
    }

    /// <summary>把导入行套到商品实体上（新增与覆盖更新共用）。**不含库存**，库存只在新建时写入。</summary>
    private static void ApplyImportFields(Product p, ProductImportRowDto r, ProductCategory category)
    {
        p.Category = category;   // 用导航属性而非外键：新分类此刻 Id 还是 0，交给 SaveChanges 补
        p.Name = (r.Name ?? "").Trim();
        p.Unit = string.IsNullOrWhiteSpace(r.Unit) ? (r.IsWeighted ? "斤" : "件") : r.Unit.Trim();
        p.Spec = string.IsNullOrWhiteSpace(r.Spec) ? null : r.Spec.Trim();
        p.SalePrice = r.SalePrice;
        p.CostPrice = r.CostPrice;
        p.SafetyStock = Math.Max(0, r.SafetyStock);
        p.ShelfLifeDays = Math.Max(0, r.ShelfLifeDays);
        p.HasExpiry = r.ShelfLifeDays > 0;
        p.IsWeighted = r.IsWeighted;
        p.Status = r.Status;
        p.Remark = string.IsNullOrWhiteSpace(r.Remark) ? null : r.Remark.Trim();
        // 拼音码是收银台首字母检索用的，改了名字必须同步重算
        p.PinyinCode = PinyinHelper.GetPinyinCode(p.Name);
    }

    /// <summary>读一行数据；整行（按已映射的列）都为空时返回 null</summary>
    private static ImportRow? ReadImportRow(IXLWorksheet ws, int row, Dictionary<string, int> colOf)
    {
        string Str(string key) => colOf.TryGetValue(key, out var c) ? ws.Cell(row, c).GetString().Trim() : "";
        decimal Num(string key) => colOf.TryGetValue(key, out var c) ? GetDecimal(ws.Cell(row, c)) : 0;

        var barcode = Str("barcode");
        var name = Str("name");
        var category = Str("category");
        var unit = Str("unit");
        var spec = Str("spec");
        var remark = Str("remark");
        var statusText = Str("status");
        var shelfLife = (int)Math.Round(Num("shelfLifeDays"));

        if (barcode.Length == 0 && name.Length == 0 && category.Length == 0 && unit.Length == 0
            && spec.Length == 0 && remark.Length == 0 && statusText.Length == 0 && shelfLife == 0
            && Num("salePrice") == 0 && Num("costPrice") == 0 && Num("stock") == 0 && Num("safetyStock") == 0)
            return null;

        return new ImportRow(row, barcode, name,
            category.Length == 0 ? null : category, unit, spec.Length == 0 ? null : spec,
            Num("salePrice"), Num("costPrice"), Num("stock"), Num("safetyStock"),
            shelfLife, ToBool(Str("isWeighted")), ToStatus(statusText), remark.Length == 0 ? null : remark);
    }

    /// <summary>读表头行：归一化后的表头文字列表 + 表头 → 列号（同名表头取最左一列）</summary>
    private static (List<string> Names, Dictionary<string, int> Cols) ReadHeaders(IXLWorksheet ws)
    {
        var names = new List<string>();
        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var c = 1; c <= lastCol; c++)
        {
            var h = NormalizeHeader(ws.Cell(1, c).GetString());
            if (h.Length == 0) continue;
            names.Add(h);
            cols.TryAdd(h, c);
        }
        return (names, cols);
    }

    /// <summary>
    /// 挑选数据工作表：跳过「使用说明」这类说明页；
    /// 有多个候选时优先选「能认出商品名称列」的那张，避免把夹带的辅助表当成数据表。
    /// </summary>
    private static IXLWorksheet? PickDataSheet(XLWorkbook wb)
    {
        var candidates = wb.Worksheets
            .Where(ws => ws.Name.Trim() is not ("使用说明" or "说明" or "导入说明" or "帮助"))
            .Where(ws => (ws.LastRowUsed()?.RowNumber() ?? 0) >= 1)
            .ToList();
        if (candidates.Count == 0) return null;

        var nameAliases = ImportFieldDefs.First(f => f.Key == "name").Aliases;
        return candidates.FirstOrDefault(ws => MatchColumn(ReadHeaders(ws).Cols.Keys, nameAliases) != null)
               ?? candidates[0];
    }

    /// <summary>表头归一化：去空白、去 * 标记与冒号、去掉括号注释，便于跟同义词比对</summary>
    private static string NormalizeHeader(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().Replace(" ", "").Replace("　", "")
            .Replace("*", "").Replace("：", "").Replace(":", "");
        s = Regex.Replace(s, @"[（(\[【].*?[）)\]】]", "");
        return s.Trim();
    }

    /// <summary>
    /// 表头自动识别：先精确匹配同义词；没命中再退化为「包含」匹配，
    /// 并按同义词长度取最长命中，避免「名称」把「商品名称」这种更具体的列抢走。
    /// </summary>
    private static string? MatchColumn(IEnumerable<string> headers, string[] aliases)
    {
        var list = headers.ToList();
        foreach (var a in aliases)
            foreach (var h in list)
                if (string.Equals(h, a, StringComparison.OrdinalIgnoreCase)) return h;

        string? best = null;
        var bestLen = 0;
        foreach (var a in aliases)
            foreach (var h in list)
                if (a.Length > bestLen && h.Contains(a, StringComparison.OrdinalIgnoreCase))
                {
                    best = h;
                    bestLen = a.Length;
                }
        return best;
    }

    /// <summary>
    /// 解析前端回传的列映射（字段 key → 表头文字）。
    /// 值为空串/null 表示「这一列不要」，会阻止该字段继续做自动识别；JSON 坏了则退回全自动识别。
    /// </summary>
    private static Dictionary<string, string?> ParseMappingJson(string? json)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        }
        catch (JsonException) { /* 忽略非法映射串 */ }
        return result;
    }

    /// <summary>是/否列：只有明确的肯定值才算 true</summary>
    private static bool ToBool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "是" or "y" or "yes" or "true" or "1" or "√" or "称重" or "散装" => true,
        _ => false,
    };

    /// <summary>状态列：只有明确的下架/停用值才算 false，空值或无法识别一律按上架处理</summary>
    private static bool ToStatus(string s) => s.Trim().ToLowerInvariant() switch
    {
        "下架" or "停用" or "禁用" or "false" or "0" or "否" or "n" => false,
        _ => true,
    };

    private static decimal GetDecimal(IXLCell cell)
    {
        if (cell.TryGetValue<decimal>(out var d)) return d;
        return decimal.TryParse(cell.GetString().Trim(), out var v) ? v : 0;
    }

    /// <summary>清理上传目录中超过指定时长的残留文件</summary>
    private static void CleanupOldUploads(string dir, TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.Now - maxAge;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { /* 清理失败不影响导入 */ }
    }
}
