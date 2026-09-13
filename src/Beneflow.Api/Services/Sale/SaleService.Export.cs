using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>销售管理（部分）：销售单列表导出。</summary>
public partial class SaleService : ISaleService
{
    /// <summary>销售单全量导出（按 keyword/dateFrom/dateTo/payMethod 筛选，不分页）</summary>
    public async Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword, string? dateFrom, string? dateTo, string? payMethod)
    {
        var q =
            from o in _db.SaleOrders.AsNoTracking()
            join u in _db.Users on o.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword)
                  || o.OrderNo.Contains(keyword) || (o.WechatId != null && o.WechatId.Contains(keyword))
            select new { o, UserName = u.Name };

        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            q = q.Where(x => x.o.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            q = q.Where(x => x.o.CreatedAt < dt.AddDays(1));
        if (!string.IsNullOrEmpty(payMethod))
            q = q.Where(x => x.o.PayMethod == payMethod);

        var rows = await q.OrderByDescending(x => x.o.Id).ToListAsync();
        var ids = rows.Select(r => r.o.Id).ToList();
        var retAgg = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => ids.Contains(d.OrderId))
            .GroupBy(d => d.OrderId)
            .Select(g => new { OrderId = g.Key, Qty = g.Sum(x => x.Quantity), Ret = g.Sum(x => x.ReturnedQuantity) })
            .ToDictionaryAsync(x => x.OrderId, x => x);

        var creditAgg = await _db.CreditSales.AsNoTracking()
            .Where(c => ids.Contains(c.SaleOrderId))
            .Select(c => new { c.SaleOrderId, c.Status })
            .ToDictionaryAsync(x => x.SaleOrderId, x => x.Status);

        return rows.Select(r =>
        {
            retAgg.TryGetValue(r.o.Id, out var agg);
            bool? settled = null;
            if (r.o.IsCredit && creditAgg.TryGetValue(r.o.Id, out var st)) settled = st;
            return new Dictionary<string, object?>
            {
                ["orderNo"] = r.o.OrderNo,
                ["totalAmount"] = r.o.TotalAmount,
                ["discountAmount"] = r.o.DiscountAmount,
                ["payAmount"] = r.o.PayAmount,
                ["payMethod"] = r.o.PayMethod,
                ["cashAmount"] = r.o.CashAmount,
                ["changeAmount"] = r.o.ChangeAmount,
                ["status"] = StatusText(r.o.IsVoided, agg?.Qty ?? 0, agg?.Ret ?? 0),
                ["isCredit"] = r.o.IsCredit ? "赊账" : "",
                ["creditSettled"] = settled switch { true => "已结", false => "欠", _ => "" },
                ["wechatId"] = r.o.WechatId ?? "",
                ["createdAt"] = r.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                ["createdByName"] = r.UserName,
            };
        }).ToList();
    }
}
