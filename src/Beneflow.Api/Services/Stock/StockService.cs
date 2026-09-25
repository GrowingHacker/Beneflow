using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Beneflow.Api.Services;

/// <summary>库存管理：实时库存 / 库存预警 / 临期商品 / 盘点单 / 库存流水</summary>
public partial class StockService : IStockService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly StockMutationLock _stockLock;

    public StockService(AppDbContext db, ICurrentUser me, StockMutationLock stockLock)
    { _db = db; _me = me; _stockLock = stockLock; }

    /// <summary>临期天数阈值（系统设置，默认 30）</summary>
    private async Task<int> ExpiryDaysAsync()
    {
        var v = await _db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == "stock").Select(c => c.ConfigValue).FirstOrDefaultAsync();
        if (v != null)
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(v);
                return json.RootElement.TryGetProperty("expiryDays", out var el) ? el.GetInt32() : 30;
            }
            catch { /* 忽略坏配置 */ }
        }
        return 30;
    }

    // ================= 实时库存（带状态标签） =================

    public async Task<object> InventoryListAsync(string? keyword, string? status, int page, int pageSize)
    {
        var expiryDays = await ExpiryDaysAsync();
        var q =
            from p in _db.Products.AsNoTracking()
            join c in _db.Categories on p.CategoryId equals c.Id
            where !p.IsDeleted &&
                  (string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) || p.Barcode.Contains(keyword))
            orderby p.Id
            select new
            {
                p.Id, p.Barcode, p.Name, CategoryName = c.Name,
                p.Unit, p.SalePrice, p.CostPrice,
                p.StockQuantity, p.SafetyStock, p.HasExpiry, p.ShelfLifeDays,
                p.IsWeighted,
            };

        var all = await q.ToListAsync();

        // 最小剩余天数（未处理批次）
        var ids = all.Select(x => x.Id).ToList();
        var minLeft = await (
            from b in _db.Batches.AsNoTracking()
            where !b.IsProcessed && ids.Contains(b.ProductId)
            group b by b.ProductId into g
            select new { Pid = g.Key, Min = g.Min(b => b.ExpireDate) }).ToDictionaryAsync(x => x.Pid, x => x.Min);

        var rows = all.Select(p =>
        {
            int? left = null;
            string st;
            DateTime? expire = null;
            if (p.HasExpiry && minLeft.TryGetValue(p.Id, out var d))
            {
                left = (int)Math.Floor((d - DateTime.Today).TotalDays);
                expire = d;
            }
            if (p.StockQuantity <= 0) st = "缺货";
            else if (p.StockQuantity <= p.SafetyStock) st = "预警";
            else if (left.HasValue && left.Value < 0) st = "过期";
            else if (left.HasValue && left.Value <= expiryDays) st = "临期";
            else st = "正常";
            return new
            {
                id = p.Id, barcode = p.Barcode, name = p.Name, categoryName = p.CategoryName,
                unit = p.Unit, costPrice = p.CostPrice, salePrice = p.SalePrice,
                stockQuantity = p.StockQuantity, safetyStock = p.SafetyStock,
                stockAmount = Math.Round(p.StockQuantity * p.CostPrice, 2),
                hasExpiry = p.HasExpiry,
                // 称重商品：库存预警「前往进货」把待补货商品带进建单弹窗时要靠它，
                // 否则弹窗按「整数件」渲染数量（最小 1、步进 1），散装重量填不进去
                isWeighted = p.IsWeighted,
                expireDate = expire?.ToString("yyyy-MM-dd"),
                daysLeft = left,
                status = st,
            };
        });

        var filtered = string.IsNullOrEmpty(status)
            ? rows.Cast<object>()
            : rows.Where(r => r.status == status).Cast<object>();

        var total = filtered.Count();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var list = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new { list, total, page, pageSize };
    }

    /// <summary>导出实时库存全量（按 keyword/status 筛选，不分页），返回行字典</summary>
    public async Task<List<Dictionary<string, object?>>> ExportInventoryAsync(string? keyword, string? status)
    {
        var expiryDays = await ExpiryDaysAsync();
        var q =
            from p in _db.Products.AsNoTracking()
            join c in _db.Categories on p.CategoryId equals c.Id
            where !p.IsDeleted &&
                  (string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) || p.Barcode.Contains(keyword))
            orderby p.Id
            select new
            {
                p.Id, p.Barcode, p.Name, CategoryName = c.Name,
                p.Unit, p.SalePrice, p.CostPrice,
                p.StockQuantity, p.SafetyStock, p.HasExpiry,
            };

        var all = await q.ToListAsync();
        var ids = all.Select(x => x.Id).ToList();
        var minLeft = await (
            from b in _db.Batches.AsNoTracking()
            where !b.IsProcessed && ids.Contains(b.ProductId)
            group b by b.ProductId into g
            select new { Pid = g.Key, Min = g.Min(b => b.ExpireDate) }).ToDictionaryAsync(x => x.Pid, x => x.Min);

        var rows = all.Select(p =>
        {
            int? left = null; string st; DateTime? expire = null;
            if (p.HasExpiry && minLeft.TryGetValue(p.Id, out var d))
            { left = (int)Math.Floor((d - DateTime.Today).TotalDays); expire = d; }
            if (p.StockQuantity <= 0) st = "缺货";
            else if (p.StockQuantity <= p.SafetyStock) st = "预警";
            else if (left.HasValue && left.Value < 0) st = "过期";
            else if (left.HasValue && left.Value <= expiryDays) st = "临期";
            else st = "正常";
            return new Dictionary<string, object?>
            {
                ["barcode"] = p.Barcode, ["name"] = p.Name, ["categoryName"] = p.CategoryName,
                ["unit"] = p.Unit, ["costPrice"] = p.CostPrice, ["salePrice"] = p.SalePrice,
                ["stockQuantity"] = p.StockQuantity, ["safetyStock"] = p.SafetyStock,
                ["stockAmount"] = Math.Round(p.StockQuantity * p.CostPrice, 2),
                ["expireDate"] = expire?.ToString("yyyy-MM-dd"),
                ["status"] = st,
            };
        });
        var filtered = string.IsNullOrEmpty(status) ? rows : rows.Where(r => (string)r["status"]! == status);
        return filtered.ToList();
    }
}
