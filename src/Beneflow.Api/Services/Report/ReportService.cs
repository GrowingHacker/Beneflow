using Beneflow.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>财务报表与首页看板</summary>
public class ReportService : IReportService
{
    private readonly AppDbContext _db;
    public ReportService(AppDbContext db) => _db = db;

    // 毛利口径：明细小计 − 明细数量×成本快照 − 订单优惠

    /// <summary>日销售报表：汇总 + 近 7 天趋势 + 当日商品 Top5</summary>
    public async Task<object> DailySalesAsync(DateTime date)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);

        var dayOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= dayStart && o.CreatedAt < dayEnd)
            .ToListAsync();
        var dayIds = dayOrders.Select(o => o.Id).ToList();
        var dayDetails = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => dayIds.Contains(d.OrderId)).ToListAsync();

        var sales = Math.Round(dayOrders.Sum(o => o.PayAmount), 2);
        var discount = Math.Round(dayOrders.Sum(o => o.DiscountAmount), 2);
        var profit = Math.Round(dayDetails.Sum(d => d.SubTotal - d.Quantity * d.CostPrice) - discount, 2);

        // 近 7 天趋势
        var trendStart = dayStart.AddDays(-6);
        var trendOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= trendStart && o.CreatedAt < dayEnd)
            .Select(o => new { o.CreatedAt, o.PayAmount }).ToListAsync();
        var trend = Enumerable.Range(0, 7).Select(i =>
        {
            var s = trendStart.AddDays(i);
            var e = s.AddDays(1);
            return new
            {
                date = s.ToString("MM-dd"),
                sales = Math.Round(trendOrders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.PayAmount), 2),
            };
        });

        // 当日 Top5（按销售额）
        var top5 = dayDetails.GroupBy(d => d.ProductName)
            .Select(g => new
            {
                name = g.Key,
                qty = g.Sum(x => x.Quantity),
                amount = Math.Round(g.Sum(x => x.SubTotal), 2),
            })
            .OrderByDescending(x => x.amount).Take(5);

        return new
        {
            summary = new
            {
                sales,
                orders = dayOrders.Count,
                avg = dayOrders.Count == 0 ? 0 : Math.Round(sales / dayOrders.Count, 2),
                profit,
            },
            trend,
            top5,
            items = dayDetails.GroupBy(d => new { d.ProductName, d.UnitPrice })
                .Select(g => new
                {
                    name = g.Key.ProductName,
                    unitPrice = g.Key.UnitPrice,
                    qty = g.Sum(x => x.Quantity),
                    amount = Math.Round(g.Sum(x => x.SubTotal), 2),
                    profit = Math.Round(g.Sum(x => x.SubTotal - x.Quantity * x.CostPrice), 2),
                })
                .OrderByDescending(x => x.amount).ToList(),
        };
    }

    /// <summary>月销售报表：按日汇总</summary>
    public async Task<object> MonthlySalesAsync(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        var orders = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= start && o.CreatedAt < end)
            .Select(o => new { o.CreatedAt, o.PayAmount, o.DiscountAmount })
            .ToListAsync();
        var ids = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= start && o.CreatedAt < end).Select(o => o.Id).ToListAsync();
        var cost = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => ids.Contains(d.OrderId))
            .SumAsync(d => d.Quantity * d.CostPrice);

        var days = Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Select(day =>
        {
            var s = new DateTime(year, month, day);
            var e = s.AddDays(1);
            var sum = orders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.PayAmount);
            return new { date = $"{month:D2}-{day:D2}", sales = Math.Round(sum, 2) };
        }).Where(d => d.sales > 0 || true);

        var totalSales = Math.Round(orders.Sum(o => o.PayAmount), 2);
        return new
        {
            total = totalSales,
            orders = orders.Count,
            profit = Math.Round(totalSales - (decimal)cost - orders.Sum(o => o.DiscountAmount), 2),
            days,
        };
    }

    /// <summary>利润分析：时间段汇总 + 分类占比</summary>
    public async Task<object> ProfitAnalysisAsync(DateTime from, DateTime to)
    {
        var to2 = to.Date.AddDays(1);
        var orders = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= from.Date && o.CreatedAt < to2)
            .ToListAsync();
        var ids = orders.Select(o => o.Id).ToList();
        var details = await (
            from d in _db.SaleOrderDetails.AsNoTracking()
            join p in _db.Products on d.ProductId equals p.Id
            join c in _db.Categories on p.CategoryId equals c.Id
            where ids.Contains(d.OrderId)
            select new { d, Category = c.Name }
        ).ToListAsync();

        var sales = Math.Round(orders.Sum(o => o.PayAmount), 2);
        var costRounded = Math.Round(details.Sum(x => x.d.Quantity * x.d.CostPrice), 2);
        var discount = Math.Round(orders.Sum(o => o.DiscountAmount), 2);
        var profit = Math.Round(sales - costRounded - discount, 2);

        var byCategory = details.GroupBy(x => x.Category).Select(g => new
        {
            category = g.Key,
            sales = Math.Round(g.Sum(x => x.d.SubTotal), 2),
            profit = Math.Round(g.Sum(x => x.d.SubTotal - x.d.Quantity * x.d.CostPrice), 2),
        }).OrderByDescending(x => x.sales);

        return new
        {
            sales, cost = costRounded, profit,
            profitRate = sales == 0 ? 0 : Math.Round(profit / sales, 4),
            byCategory,
        };
    }

    /// <summary>供应商对账单：时间范围内的进货单列表 + 合计</summary>
    public async Task<object> SupplierStatementAsync(int supplierId, string? dateFrom, string? dateTo)
    {
        var q =
            from po in _db.PurchaseOrders.AsNoTracking()
            join sup in _db.Suppliers on po.SupplierId equals sup.Id
            where po.SupplierId == supplierId
            orderby po.CreatedAt descending
            select new { po, sup.Name };

        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            q = q.Where(x => x.po.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            q = q.Where(x => x.po.CreatedAt < dt.AddDays(1));

        var rows = await q.Select(x => new
        {
            id = x.po.Id, orderNo = x.po.OrderNo, supplierName = x.Name,
            totalQty = x.po.TotalQty, totalAmount = x.po.TotalAmount,
            createdAt = x.po.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToListAsync();

        return new
        {
            items = rows,
            count = rows.Count,
            total = Math.Round(rows.Sum(r => r.totalAmount), 2),
        };
    }

    /// <summary>赊账汇总：按微信号聚合 + 时间段统计</summary>
    public async Task<object> CreditSummaryAsync(string? dateFrom, string? dateTo)
    {
        var q = _db.CreditSales.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            q = q.Where(c => c.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            q = q.Where(c => c.CreatedAt < dt.AddDays(1));

        var byWechat = await q.GroupBy(c => c.WechatId).Select(g => new
        {
            wechatId = g.Key,
            creditTotal = Math.Round(g.Sum(x => x.CreditAmount), 2),
            paidTotal = Math.Round(g.Sum(x => x.PaidAmount), 2),
            remainingTotal = Math.Round(g.Sum(x => x.RemainingAmount), 2),
            unsettledCount = g.Count(x => !x.Status),
        }).OrderByDescending(x => x.remainingTotal).ToListAsync();

        var stats = await q.GroupBy(_ => 1).Select(g => new
        {
            TotalCredit = g.Sum(x => x.CreditAmount),
            PaidCredit = g.Sum(x => x.PaidAmount),
            UnpaidCredit = g.Sum(x => x.RemainingAmount),
        }).FirstOrDefaultAsync();

        return new
        {
            byWechat,
            totalCredit = Math.Round(stats?.TotalCredit ?? 0, 2),
            paidCredit = Math.Round(stats?.PaidCredit ?? 0, 2),
            unpaidCredit = Math.Round(stats?.UnpaidCredit ?? 0, 2),
        };
    }

    /// <summary>临期天数阈值（系统设置 stock 组，解析失败默认 30）</summary>
    public async Task<int> GetExpiryDaysAsync()
    {
        var v = await _db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == "stock").Select(c => c.ConfigValue).FirstOrDefaultAsync();
        try
        {
            if (v != null)
            {
                var doc = System.Text.Json.JsonDocument.Parse(v);
                if (doc.RootElement.TryGetProperty("expiryDays", out var el)) return el.GetInt32();
            }
        }
        catch { }
        return 30;
    }

    /// <summary>首页看板：今日/本月实时数据 + 预警 + 赊账提醒 + 趋势 + Top5</summary>
    public async Task<object> DashboardSummaryAsync(int expiryDays = 30)
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var monthStart = new DateTime(today.Year, today.Month, 1);

        // 今日
        var todayOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= today && o.CreatedAt < tomorrow).ToListAsync();
        var todayIds = todayOrders.Select(o => o.Id).ToList();
        var todayDetails = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => todayIds.Contains(d.OrderId)).ToListAsync();
        var todaySales = Math.Round(todayOrders.Sum(o => o.PayAmount), 2);
        var todayDiscount = Math.Round(todayOrders.Sum(o => o.DiscountAmount), 2);

        // 本月
        var monthOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= monthStart && o.CreatedAt < tomorrow)
            .Select(o => new { o.Id, o.PayAmount, o.DiscountAmount }).ToListAsync();
        var monthIds = monthOrders.Select(o => o.Id).ToList();
        var monthCost = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => monthIds.Contains(d.OrderId)).SumAsync(d => d.Quantity * d.CostPrice);

        // 库存预警卡片
        var shortage = await _db.Products.CountAsync(p => !p.IsDeleted && p.StockQuantity <= 0);
        var warning = await _db.Products.CountAsync(p => !p.IsDeleted && p.StockQuantity > 0 && p.StockQuantity <= p.SafetyStock);
        var expiryBound = today.AddDays(expiryDays);
        var expiring = await _db.Batches.CountAsync(b => !b.IsProcessed && b.ExpireDate <= expiryBound && b.ExpireDate >= today);
        var expiredCnt = await _db.Batches.CountAsync(b => !b.IsProcessed && b.ExpireDate < today);

        // 赊账提醒
        var credits = await _db.CreditSales.AsNoTracking().ToListAsync();

        // 趋势 + Top5
        var trendStart = today.AddDays(-6);
        var trendOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= trendStart && o.CreatedAt < tomorrow)
            .Select(o => new { o.CreatedAt, o.PayAmount }).ToListAsync();
        var trend = Enumerable.Range(0, 7).Select(i =>
        {
            var s = trendStart.AddDays(i);
            var e = s.AddDays(1);
            return new
            {
                date = s.ToString("MM-dd"),
                sales = Math.Round(trendOrders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.PayAmount), 2),
            };
        });

        var monthTopStart = today.AddDays(-30);
        var top5 = await (
            from d in _db.SaleOrderDetails.AsNoTracking()
            join o in _db.SaleOrders on d.OrderId equals o.Id
            where o.CreatedAt >= monthTopStart
            group d by d.ProductName into g
            orderby g.Sum(x => x.SubTotal) descending
            select new { name = g.Key, qty = g.Sum(x => x.Quantity), amount = Math.Round(g.Sum(x => x.SubTotal), 2) }
        ).Take(5).ToListAsync();

        return new
        {
            today = new
            {
                sales = todaySales,
                orders = todayOrders.Count,
                avg = todayOrders.Count == 0 ? 0 : Math.Round(todaySales / todayOrders.Count, 2),
                profit = Math.Round(todayDetails.Sum(d => d.SubTotal - d.Quantity * d.CostPrice) - todayDiscount, 2),
            },
            month = new
            {
                sales = Math.Round(monthOrders.Sum(o => o.PayAmount), 2),
                orders = monthOrders.Count,
                profit = Math.Round(monthOrders.Sum(o => o.PayAmount) - monthCost - monthOrders.Sum(o => o.DiscountAmount), 2),
            },
            stock = new { shortage, warning, expiry = expiring + expiredCnt },
            credit = new
            {
                total = Math.Round(credits.Where(c => !c.Status).Sum(c => c.RemainingAmount), 2),
                unsettled = credits.Count(c => !c.Status),
            },
            trend,
            top5,
        };
    }
}
