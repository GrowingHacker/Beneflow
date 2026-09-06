using System.Text.Json;
using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>商品档案与分类</summary>
public class ProductService : IProductService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly ILogService _logs;

    public ProductService(AppDbContext db, ICurrentUser me, ILogService logs) { _db = db; _me = me; _logs = logs; }

    public async Task<PagedResult<object>> ListAsync(string? keyword, int page, int pageSize)
    {
        var q = _db.Products.AsNoTracking()
            .Where(p => !p.IsDeleted &&
                (string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) || p.Barcode.Contains(keyword) || p.PinyinCode.Contains(keyword.ToUpper())))
            .Select(p => new
            {
                p.Id, p.Barcode, p.Name, p.CategoryId,
                CategoryName = _db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                p.Unit, p.Spec, p.SalePrice, p.CostPrice,
                p.StockQuantity, p.SafetyStock, p.ImageUrl,
                p.HasExpiry, p.ShelfLifeDays, p.IsWeighted, p.PinyinCode, p.Status, p.CreatedAt,
            });

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.OrderByDescending(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        // 最近未处理批次的最早到期日（临期角标用）
        var ids = rows.Select(r => r.Id).ToList();
        var expMap = await _db.Batches.AsNoTracking()
            .Where(b => !b.IsProcessed && ids.Contains(b.ProductId))
            .GroupBy(b => b.ProductId)
            .Select(g => new { Pid = g.Key, Min = g.Min(b => b.ExpireDate) })
            .ToDictionaryAsync(x => x.Pid, x => x.Min);

        var list = rows.Select(r =>
        {
            var dict = new Dictionary<string, object?>
            {
                ["id"] = r.Id, ["barcode"] = r.Barcode, ["name"] = r.Name,
                ["categoryId"] = r.CategoryId, ["categoryName"] = r.CategoryName,
                ["unit"] = r.Unit, ["spec"] = r.Spec,
                ["salePrice"] = r.SalePrice, ["costPrice"] = r.CostPrice,
                ["stockQuantity"] = r.StockQuantity, ["safetyStock"] = r.SafetyStock,
                ["imageUrl"] = r.ImageUrl, ["hasExpiry"] = r.HasExpiry,
                ["shelfLifeDays"] = r.ShelfLifeDays,
                ["isWeighted"] = r.IsWeighted,
                ["pinyinCode"] = r.PinyinCode,
                ["status"] = r.Status ? "上架" : "下架",
                ["createdAt"] = r.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            };
            if (r.HasExpiry && expMap.TryGetValue(r.Id, out var expire))
                dict["expireDate"] = expire.ToString("yyyy-MM-dd");
            return dict as object;
        }).ToList();

        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>导出全量（按 keyword 筛选，不分页）。字段与列表一致，供 Excel 渲染</summary>
    public async Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword)
    {
        var rows = await _db.Products.AsNoTracking()
            .Where(p => !p.IsDeleted &&
                (string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) || p.Barcode.Contains(keyword)))
            .Select(p => new
            {
                p.Id, p.Barcode, p.Name, p.CategoryId,
                CategoryName = _db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                p.Unit, p.Spec, p.SalePrice, p.CostPrice,
                p.StockQuantity, p.HasExpiry, p.Status,
            })
            .OrderByDescending(p => p.Id).ToListAsync();

        var ids = rows.Select(r => r.Id).ToList();
        var expMap = await _db.Batches.AsNoTracking()
            .Where(b => !b.IsProcessed && ids.Contains(b.ProductId))
            .GroupBy(b => b.ProductId)
            .Select(g => new { Pid = g.Key, Min = g.Min(b => b.ExpireDate) })
            .ToDictionaryAsync(x => x.Pid, x => x.Min);

        return rows.Select(r =>
        {
            var dict = new Dictionary<string, object?>
            {
                ["barcode"] = r.Barcode, ["name"] = r.Name,
                ["categoryName"] = r.CategoryName, ["unit"] = r.Unit, ["spec"] = r.Spec,
                ["salePrice"] = r.SalePrice, ["costPrice"] = r.CostPrice,
                ["stockQuantity"] = r.StockQuantity, ["status"] = r.Status ? "上架" : "下架",
            };
            if (r.HasExpiry && expMap.TryGetValue(r.Id, out var expire))
                dict["expireDate"] = expire.ToString("yyyy-MM-dd");
            return dict;
        }).ToList();
    }

    /// <summary>条码查询商品（收银 / 手机进货），字段与列表一致</summary>
    public async Task<ApiResult<object?>> GetByBarcode(string barcode)
    {
        var row = await _db.Products.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Barcode == barcode)
            .Select(p => new
            {
                p.Id, p.Barcode, p.Name, p.CategoryId,
                CategoryName = _db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                p.Unit, p.Spec, p.SalePrice, p.CostPrice,
                p.StockQuantity, p.SafetyStock, p.HasExpiry, p.ShelfLifeDays,
                p.IsWeighted, p.PinyinCode,
                Status = p.Status ? "上架" : "下架",
            })
            .Take(1).ToListAsync();
        if (row.Count == 0) return ApiResult<object?>.Fail("商品不存在");
        return ApiResult<object?>.Ok(row[0]);
    }

    /// <summary>获取称重商品列表（收银台快捷面板用），按分类分组返回</summary>
    public async Task<List<object>> GetWeightedProductsAsync()
    {
        var items = await _db.Products.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Status && p.IsWeighted)
            .Select(p => new
            {
                p.Id, p.Name, p.Barcode, p.PinyinCode,
                p.SalePrice, p.Unit, p.StockQuantity,
                CategoryName = _db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault() ?? "未分类",
            })
            .OrderBy(p => p.CategoryName).ThenBy(p => p.Name)
            .ToListAsync();
        return items.Cast<object>().ToList();
    }

    public async Task<ApiResult<object>> CreateAsync(ProductUpsertDto dto)
    {
        var cat = await ResolveCategoryAsync(dto.CategoryId, dto.CategoryName);
        if (cat == null) return ApiResult<object>.Fail("分类不存在");

        var barcode = dto.Barcode?.Trim() ?? "";
        if (string.IsNullOrEmpty(barcode)) barcode = await NewInStoreBarcodeAsync();
        else if (await _db.Products.AnyAsync(p => !p.IsDeleted && p.Barcode == barcode))
            return ApiResult<object>.Fail($"条码 {barcode} 已存在");
        if (dto.SalePrice < 0 || dto.CostPrice < 0) return ApiResult<object>.Fail("价格不能为负");

        var p = new Product
        {
            Barcode = barcode, Name = dto.Name.Trim(), CategoryId = cat.Id,
            Unit = dto.Unit, Spec = dto.Spec, SalePrice = dto.SalePrice, CostPrice = dto.CostPrice,
            StockQuantity = Math.Max(0, dto.StockQuantity), SafetyStock = Math.Max(0, dto.SafetyStock),
            HasExpiry = dto.HasExpiry, ShelfLifeDays = dto.ShelfLifeDays,
            IsWeighted = dto.IsWeighted, PinyinCode = PinyinHelper.GetPinyinCode(dto.Name),
            Status = dto.Status, Remark = dto.Remark,
        };
        _db.Products.Add(p);
        await _db.SaveChangesAsync();

        if (p.StockQuantity > 0)
        {
            _db.StockLogs.Add(new StockLog
            {
                ProductId = p.Id, ChangeType = "期初建账", ChangeQty = p.StockQuantity,
                BeforeQty = 0, AfterQty = p.StockQuantity, RefNo = "INIT",
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
            await _db.SaveChangesAsync();
        }

        await _logs.WriteAsync("商品管理", "新增商品", $"{p.Barcode} {p.Name}");
        await _db.SaveChangesAsync();
        return ApiResult<object>.Ok(new { id = p.Id, barcode = p.Barcode });
    }

    public async Task<ApiResult> UpdateAsync(int id, JsonElement body)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (p == null) return ApiResult.Fail("商品不存在");

        bool changed = false;

        // ---- 分类：categoryId 或 categoryName（支持新建分类）----
        int categoryId = 0;
        string? categoryName = null;
        if (body.TryGetProperty("categoryId", out var cidEl))
        {
            if (cidEl.ValueKind == JsonValueKind.Number) categoryId = cidEl.GetInt32();
        }
        if (body.TryGetProperty("categoryName", out var cnameEl))
        {
            categoryName = cnameEl.GetString();
        }
        if (categoryId > 0 || !string.IsNullOrEmpty(categoryName))
        {
            var cat = await ResolveCategoryAsync(categoryId, categoryName);
            if (cat == null) return ApiResult.Fail("分类不存在");
            if (p.CategoryId != cat.Id) { p.CategoryId = cat.Id; changed = true; }
        }

        // ---- 基础字段：只在 JSON 中出现时才更新 ----
        if (body.TryGetProperty("name", out var nameEl))
        {
            var name = nameEl.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && p.Name != name)
            {
                p.Name = name;
                p.PinyinCode = PinyinHelper.GetPinyinCode(name);
                changed = true;
            }
        }
        if (body.TryGetProperty("barcode", out var bcEl))
        {
            var bc = bcEl.GetString()?.Trim() ?? "";
            if (p.Barcode != bc)
            {
                // 非空条码需要检查唯一性（空条码允许重复）
                if (!string.IsNullOrEmpty(bc))
                {
                    if (await _db.Products.AnyAsync(x => x.Id != id && !x.IsDeleted && x.Barcode == bc))
                        return ApiResult.Fail($"条码 {bc} 已被其他商品使用");
                }
                p.Barcode = bc;
                changed = true;
            }
        }
        if (body.TryGetProperty("unit", out var unitEl))
        {
            var unit = unitEl.GetString();
            if (p.Unit != unit) { p.Unit = unit ?? ""; changed = true; }
        }
        if (body.TryGetProperty("spec", out var specEl))
        {
            var spec = specEl.GetString();
            if (p.Spec != spec) { p.Spec = spec; changed = true; }
        }
        if (body.TryGetProperty("salePrice", out var spEl) && spEl.ValueKind == JsonValueKind.Number)
        {
            var sp = spEl.GetDecimal();
            if (sp < 0) return ApiResult.Fail("价格不能为负");
            if (p.SalePrice != sp) { p.SalePrice = sp; changed = true; }
        }
        if (body.TryGetProperty("costPrice", out var cpEl) && cpEl.ValueKind == JsonValueKind.Number)
        {
            var cp = cpEl.GetDecimal();
            if (cp < 0) return ApiResult.Fail("价格不能为负");
            if (p.CostPrice != cp) { p.CostPrice = cp; changed = true; }
        }
        if (body.TryGetProperty("safetyStock", out var ssEl) && ssEl.ValueKind == JsonValueKind.Number)
        {
            var ss = Math.Max(0, ssEl.GetDecimal());
            if (p.SafetyStock != ss) { p.SafetyStock = ss; changed = true; }
        }
        if (body.TryGetProperty("hasExpiry", out var heEl) && (heEl.ValueKind == JsonValueKind.True || heEl.ValueKind == JsonValueKind.False))
        {
            var he = heEl.GetBoolean();
            if (p.HasExpiry != he) { p.HasExpiry = he; changed = true; }
        }
        if (body.TryGetProperty("shelfLifeDays", out var sldEl) && sldEl.ValueKind == JsonValueKind.Number)
        {
            var sld = sldEl.GetInt32();
            if (p.ShelfLifeDays != sld) { p.ShelfLifeDays = sld; changed = true; }
        }
        if (body.TryGetProperty("isWeighted", out var iwEl) && (iwEl.ValueKind == JsonValueKind.True || iwEl.ValueKind == JsonValueKind.False))
        {
            var iw = iwEl.GetBoolean();
            if (p.IsWeighted != iw) { p.IsWeighted = iw; changed = true; }
        }
        if (body.TryGetProperty("remark", out var rkEl))
        {
            var rk = rkEl.GetString();
            if (p.Remark != rk) { p.Remark = rk; changed = true; }
        }

        // ---- 状态：兼容 bool 或 字符串 "上架"/"下架" ----
        if (body.TryGetProperty("status", out var stEl))
        {
            bool newStatus;
            if (stEl.ValueKind == JsonValueKind.True || stEl.ValueKind == JsonValueKind.False)
                newStatus = stEl.GetBoolean();
            else
            {
                var st = stEl.GetString();
                newStatus = st switch
                {
                    "上架" or "启用" or "1" => true,
                    "" or null => true,
                    string s when bool.TryParse(s, out var b) => b,
                    _ => false
                };
            }
            if (p.Status != newStatus) { p.Status = newStatus; changed = true; }
        }

        if (!changed) return ApiResult.Ok();
        p.UpdatedAt = DateTime.Now;
        await _logs.WriteAsync("商品管理", "编辑商品", $"{p.Barcode} {p.Name}");
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    public async Task<ApiResult> DeleteAsync(int id)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        if (p == null) return ApiResult.Fail("商品不存在");
        p.IsDeleted = true;
        p.Status = false;
        p.UpdatedAt = DateTime.Now;
        await _logs.WriteAsync("商品管理", "删除商品", $"{p.Barcode} {p.Name}");
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    // ---------- 分类 ----------
    public Task<List<ProductCategory>> CategoriesAsync() =>
        _db.Categories.AsNoTracking().OrderBy(c => c.Sort).ThenBy(c => c.Id).ToListAsync();

    public async Task<ApiResult<object>> CreateCategoryAsync(string name, int? parentId)
    {
        name = name?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return ApiResult<object>.Fail("请填写分类名称");
        if (await _db.Categories.AnyAsync(c => c.Name == name))
            return ApiResult<object>.Fail("分类名称已存在");
        var c = new ProductCategory { Name = name, ParentId = parentId };
        _db.Categories.Add(c);
        await _db.SaveChangesAsync();
        return ApiResult<object>.Ok(new { id = c.Id });
    }

    public async Task<ApiResult> DeleteCategoryAsync(int id)
    {
        if (await _db.Products.AnyAsync(p => !p.IsDeleted && p.CategoryId == id))
            return ApiResult.Fail("该分类下存在商品，禁止删除");
        if (await _db.Categories.AnyAsync(c => c.ParentId == id))
            return ApiResult.Fail("该分类下存在子分类，禁止删除");
        var c = await _db.Categories.FindAsync(id);
        if (c == null) return ApiResult.Fail("分类不存在");
        _db.Categories.Remove(c);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    // ---------- helpers ----------
    private async Task<ProductCategory?> ResolveCategoryAsync(int categoryId, string? categoryName)
    {
        if (categoryId > 0) return await _db.Categories.FindAsync(categoryId);
        var name = categoryName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        var exists = await _db.Categories.FirstOrDefaultAsync(c => c.Name == name);
        if (exists != null) return exists;
        var created = new ProductCategory { Name = name };
        _db.Categories.Add(created);
        await _db.SaveChangesAsync();
        return created;
    }

    /// <summary>无条码时自动生成店内码：L + yyMMddHHmmssfff + 随机两位</summary>
    private async Task<string> NewInStoreBarcodeAsync()
    {
        while (true)
        {
            var code = "L" + DateTime.Now.ToString("yyMMddHHmmssfff") + Random.Shared.Next(10, 99);
            if (!await _db.Products.AnyAsync(p => p.Barcode == code)) return code;
        }
    }
}
