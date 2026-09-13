using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>采购管理（部分）：列表导出。</summary>
public partial class PurchaseService : IPurchaseService
{
    /// <summary>进货单全量导出（按 keyword/dateFrom/dateTo 筛选，不分页）</summary>
    public async Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword, string? dateFrom, string? dateTo)
    {
        var q =
            from o in _db.PurchaseOrders.AsNoTracking()
            join s in _db.Suppliers on o.SupplierId equals s.Id
            join u in _db.Users on o.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword)
                  || o.OrderNo.Contains(keyword) || s.Name.Contains(keyword) || u.Name.Contains(keyword)
            select new { o, SupplierName = s.Name, UserName = u.Name };

        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            q = q.Where(x => x.o.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            q = q.Where(x => x.o.CreatedAt < dt.AddDays(1));

        var rows = await q.OrderByDescending(x => x.o.Id).ToListAsync();
        var orderIds = rows.Select(r => r.o.Id).ToList();
        var kindMap = await _db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => orderIds.Contains(d.OrderId))
            .GroupBy(d => d.OrderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        return rows.Select(r => new Dictionary<string, object?>
        {
            ["orderNo"] = r.o.OrderNo,
            ["supplierName"] = r.SupplierName,
            ["itemCount"] = kindMap.GetValueOrDefault(r.o.Id),
            ["totalQty"] = r.o.TotalQty,
            ["totalAmount"] = r.o.TotalAmount,
            ["remark"] = r.o.Remark ?? "",
            ["createdAt"] = r.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            ["createdByName"] = r.UserName,
        }).ToList();
    }
}
