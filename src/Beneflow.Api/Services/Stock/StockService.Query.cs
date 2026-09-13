using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Beneflow.Api.Services;

/// <summary>库存管理（部分）：库存预警 + 库存流水。</summary>
public partial class StockService : IStockService
{

    // ================= 库存预警 =================

    public async Task<List<object>> WarningsAsync()
    {
        var rows = await (
            from p in _db.Products.AsNoTracking()
            join c in _db.Categories on p.CategoryId equals c.Id
            where !p.IsDeleted && p.StockQuantity <= p.SafetyStock
            orderby p.StockQuantity ascending
            select new { p, CategoryName = c.Name }).ToListAsync();

        return rows.Select(r => (object)new
        {
            id = r.p.Id, barcode = r.p.Barcode, name = r.p.Name,
            categoryName = r.CategoryName, unit = r.p.Unit,
            stockQuantity = r.p.StockQuantity, safetyStock = r.p.SafetyStock,
            shortage = Math.Max(0, r.p.SafetyStock - r.p.StockQuantity),   // 建议补货数量
            costPrice = r.p.CostPrice, salePrice = r.p.SalePrice,
            status = r.p.StockQuantity <= 0 ? "缺货" : "预警",
        }).ToList();
    }

    // ================= 库存流水 =================

    public async Task<PagedResult<object>> LogListAsync(string? keyword, string? changeType, int page, int pageSize)
    {
        var q =
            from l in _db.StockLogs.AsNoTracking()
            join p in _db.Products on l.ProductId equals p.Id
            join u in _db.Users on l.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) ||
                  p.Barcode.Contains(keyword) || l.RefNo.Contains(keyword)
            select new { l, p.Name, p.Barcode, UserName = u.Name };

        if (!string.IsNullOrEmpty(changeType)) q = q.Where(x => x.l.ChangeType == changeType);

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.OrderByDescending(x => x.l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(r => (object)new
        {
            id = r.l.Id, productName = r.Name, barcode = r.Barcode,
            changeType = r.l.ChangeType, changeQty = r.l.ChangeQty,
            beforeQty = r.l.BeforeQty, afterQty = r.l.AfterQty,
            refNo = r.l.RefNo, createdAt = r.l.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            createdByName = r.UserName,
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>导出库存流水全量（按 keyword/changeType 筛选，不分页），返回行字典</summary>
    public async Task<List<Dictionary<string, object?>>> ExportLogAsync(string? keyword, string? changeType)
    {
        var q =
            from l in _db.StockLogs.AsNoTracking()
            join p in _db.Products on l.ProductId equals p.Id
            join u in _db.Users on l.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) ||
                  p.Barcode.Contains(keyword) || l.RefNo.Contains(keyword)
            select new { l, p.Name, p.Barcode, UserName = u.Name };
        if (!string.IsNullOrEmpty(changeType)) q = q.Where(x => x.l.ChangeType == changeType);
        var rows = await q.OrderByDescending(x => x.l.Id).ToListAsync();
        return rows.Select(r => new Dictionary<string, object?>
        {
            ["createdAt"] = r.l.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            ["productName"] = r.Name, ["barcode"] = r.Barcode,
            ["changeType"] = r.l.ChangeType, ["changeQty"] = r.l.ChangeQty,
            ["beforeQty"] = r.l.BeforeQty, ["afterQty"] = r.l.AfterQty,
            ["refNo"] = r.l.RefNo, ["createdByName"] = r.UserName,
        }).ToList();
    }
}
