using Beneflow.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>财务报表与首页看板</summary>
public class ReportService : IReportService
{
    private readonly AppDbContext _db;
    public ReportService(AppDbContext db) => _db = db;

    // 口径约定（零售行业惯例）：
    //   销售额 = 有效单据（IsVoided=false）应收合计 − 同期退货退款额
    //   实收金额 = 有效单据实收合计（赊账挂账部分天然为 0，回收走赊账核销）
    //   毛利 = Σ(明细数量×挂牌价快照 − 明细数量×成本快照) − 优惠 − 抹零 − (退货退款 − 退回商品成本)
    // ⚠️ 收入侧必须取「挂牌价」而不是成交小计：单据的 TotalAmount 也是原价合计，而 DiscountAmount
    //    里含档案促销让利（原价合计 − 成交合计）。若收入侧取成交小计，让利会被减两次，毛利凭空少一块。
    //    取挂牌价后公式等价于「Σ应收 − 成本 − 退货净额」，与改口径前数值一致（见 MEMORY 2026-09-20）。

    /// <summary>
    /// 时间段内的销售退货明细行：退货时间 / 该行退款额 / 该行退回商品成本（按原单成本快照）。
    /// 用明细行而非退货单头，既避免重复计数，也便于按天分摊退款与成本冲回。
    /// </summary>
    private async Task<List<(DateTime At, decimal Refund, decimal Cost)>> LoadReturnsAsync(DateTime startInclusive, DateTime endExclusive)
    {
        var rows = await (
            from rd in _db.SaleReturnDetails.AsNoTracking()
            join r in _db.SaleReturns.AsNoTracking() on rd.ReturnId equals r.Id
            join d in _db.SaleOrderDetails.AsNoTracking() on rd.SaleOrderDetailId equals d.Id
            // 参数不能叫 from：查询表达式里 `x.Prop >= from` 会被当成查询子句关键字，报 CS1525
            where r.CreatedAt >= startInclusive && endExclusive > r.CreatedAt
            select new { r.CreatedAt, rd.SubTotal, Cost = rd.Qty * d.CostPrice }
        ).ToListAsync();
        return rows.Select(x => (x.CreatedAt, x.SubTotal, x.Cost)).ToList();
    }

    /// <summary>日销售报表：汇总 + 近 7 天趋势 + 当日商品 Top5</summary>
    public async Task<object> DailySalesAsync(DateTime date)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);

        // 已作废单据不计入销售额/订单数/客单价/毛利
        var dayOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => !o.IsVoided && o.CreatedAt >= dayStart && o.CreatedAt < dayEnd)
            .ToListAsync();
        var dayIds = dayOrders.Select(o => o.Id).ToList();
        var dayDetails = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => dayIds.Contains(d.OrderId)).ToListAsync();

        var dayReturns = await LoadReturnsAsync(dayStart, dayEnd);
        var refund = Math.Round(dayReturns.Sum(x => x.Refund), 2);
        var refundCost = Math.Round(dayReturns.Sum(x => x.Cost), 2);

        var sales = Math.Round(dayOrders.Sum(o => o.PayAmount) - refund, 2);
        var received = Math.Round(dayOrders.Sum(o => o.ReceivedAmount), 2);
        var discount = Math.Round(dayOrders.Sum(o => o.DiscountAmount + o.RoundOffAmount), 2);
        // 收入侧取挂牌价快照：DiscountAmount 含档案促销让利，这里若取成交小计会把让利减两次
        var profit = Math.Round(dayDetails.Sum(d => d.Quantity * d.OriginalPrice - d.Quantity * d.CostPrice)
                                - discount - (refund - refundCost), 2);

        // 近 7 天趋势
        var trendStart = dayStart.AddDays(-6);
        var trendOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => !o.IsVoided && o.CreatedAt >= trendStart && o.CreatedAt < dayEnd)
            .Select(o => new { o.CreatedAt, o.PayAmount, o.ReceivedAmount }).ToListAsync();
        var trendReturns = await LoadReturnsAsync(trendStart, dayEnd);
        var trend = Enumerable.Range(0, 7).Select(i =>
        {
            var s = trendStart.AddDays(i);
            var e = s.AddDays(1);
            var gross = trendOrders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.PayAmount);
            var back = trendReturns.Where(r => r.At >= s && r.At < e).Sum(r => r.Refund);
            return new
            {
                date = s.ToString("MM-dd"),
                sales = Math.Round(gross - back, 2),
                received = Math.Round(trendOrders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.ReceivedAmount), 2),
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
                received,
                orders = dayOrders.Count,
                avg = dayOrders.Count == 0 ? 0 : Math.Round(sales / dayOrders.Count, 2),
                refund,
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
        // 已作废单据不计入销售额/订单数/毛利
        var orders = await _db.SaleOrders.AsNoTracking()
            .Where(o => !o.IsVoided && o.CreatedAt >= start && o.CreatedAt < end)
            .Select(o => new { o.CreatedAt, o.PayAmount, o.ReceivedAmount, o.DiscountAmount, o.RoundOffAmount })
            .ToListAsync();
        var ids = await _db.SaleOrders.AsNoTracking()
            .Where(o => !o.IsVoided && o.CreatedAt >= start && o.CreatedAt < end).Select(o => o.Id).ToListAsync();
        // 原价合计与成本合计：毛利 = (原价 − 成本) − 优惠 − 抹零 − 退货净额
        var listAmount = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => ids.Contains(d.OrderId))
            .SumAsync(d => d.Quantity * d.OriginalPrice);
        var cost = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => ids.Contains(d.OrderId))
            .SumAsync(d => d.Quantity * d.CostPrice);

        var monthReturns = await LoadReturnsAsync(start, end);
        var refund = Math.Round(monthReturns.Sum(x => x.Refund), 2);
        var refundCost = Math.Round(monthReturns.Sum(x => x.Cost), 2);

        var days = Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Select(day =>
        {
            var s = new DateTime(year, month, day);
            var e = s.AddDays(1);
            var gross = orders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.PayAmount);
            var back = monthReturns.Where(r => r.At >= s && r.At < e).Sum(r => r.Refund);
            return new
            {
                date = $"{month:D2}-{day:D2}",
                sales = Math.Round(gross - back, 2),
                received = Math.Round(orders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.ReceivedAmount), 2),
            };
        }).Where(d => d.sales > 0 || true);

        var totalSales = Math.Round(orders.Sum(o => o.PayAmount) - refund, 2);
        return new
        {
            total = totalSales,
            received = Math.Round(orders.Sum(o => o.ReceivedAmount), 2),
            refund,
            orders = orders.Count,
            // 收入侧取原价合计：totalSales 已是「原价 − 优惠 − 抹零 − 退货」后的数，
            // 再减一次优惠/抹零会重复扣（原写法就是这个错，现金单的抹零被减了两遍）
            profit = Math.Round((decimal)listAmount - (decimal)cost
                                - orders.Sum(o => o.DiscountAmount + o.RoundOffAmount)
                                - (refund - refundCost), 2),
            days,
        };
    }

    /// <summary>利润分析：时间段汇总 + 分类占比</summary>
    public async Task<object> ProfitAnalysisAsync(DateTime from, DateTime to)
    {
        var to2 = to.Date.AddDays(1);
        // 已作废单据不计入销售额/成本/毛利
        var orders = await _db.SaleOrders.AsNoTracking()
            .Where(o => !o.IsVoided && o.CreatedAt >= from.Date && o.CreatedAt < to2)
            .ToListAsync();
        var ids = orders.Select(o => o.Id).ToList();
        var details = await (
            from d in _db.SaleOrderDetails.AsNoTracking()
            join p in _db.Products on d.ProductId equals p.Id
            join c in _db.Categories on p.CategoryId equals c.Id
            where ids.Contains(d.OrderId)
            select new { d, Category = c.Name }
        ).ToListAsync();

        var periodReturns = await LoadReturnsAsync(from.Date, to2);
        var refund = Math.Round(periodReturns.Sum(x => x.Refund), 2);
        var refundCost = Math.Round(periodReturns.Sum(x => x.Cost), 2);

        var sales = Math.Round(orders.Sum(o => o.PayAmount) - refund, 2);
        var received = Math.Round(orders.Sum(o => o.ReceivedAmount), 2);
        var costRounded = Math.Round(details.Sum(x => x.d.Quantity * x.d.CostPrice), 2);
        // 原价合计：与成本同取自明细，保证「收入 − 成本 − 优惠 − 抹零 − 退货净额」不重复扣
        var listRounded = Math.Round(details.Sum(x => x.d.Quantity * x.d.OriginalPrice), 2);
        var discount = Math.Round(orders.Sum(o => o.DiscountAmount + o.RoundOffAmount), 2);
        var profit = Math.Round(listRounded - costRounded - discount - (refund - refundCost), 2);

        var byCategory = details.GroupBy(x => x.Category).Select(g => new
        {
            category = g.Key,
            sales = Math.Round(g.Sum(x => x.d.SubTotal), 2),
            profit = Math.Round(g.Sum(x => x.d.SubTotal - x.d.Quantity * x.d.CostPrice), 2),
        }).OrderByDescending(x => x.sales);

        return new
        {
            sales, received, refund, cost = costRounded, profit,
            profitRate = sales == 0 ? 0 : Math.Round(profit / sales, 4),
            byCategory,
        };
    }

    /// <summary>
    /// 供应商对账单：时间范围内的**进货单 + 采购退货单**流水，合计给「净应付」。
    ///
    /// 口径（2026-09-22 定稿）：对账单回答的是「我欠这家供应商多少钱」，所以两个方向都要进 ——
    /// 进货让欠款增加（金额记**正**），采购退货让欠款减少（金额记**负**），
    /// `netPayable = purchaseTotal − returnTotal` 就是净应付。
    /// 之前只列进货单，退回去的货等于白欠着，账对不上（见项目笔记的已知缺口）。
    ///
    /// 金额带符号还有一个用处：**「按顺序求和 ＝ 合计」这条恒等式成立** ——
    /// 屏幕上/导出的每一行加起来就是最后那个净应付，对账单最该一眼看懂的就是这件事。
    /// 已作废的进货单与退货单一律剔除（作废单不回退任何欠款，也不该出现在对账里）。
    /// </summary>
    public async Task<object> SupplierStatementAsync(int supplierId, string? dateFrom, string? dateTo)
    {
        var purchaseQ =
            from po in _db.PurchaseOrders.AsNoTracking()
            join sup in _db.Suppliers on po.SupplierId equals sup.Id
            where po.SupplierId == supplierId && !po.IsVoided
            select new { po.OrderNo, sup.Name, po.TotalQty, po.TotalAmount, po.CreatedAt };

        var returnQ =
            from pr in _db.PurchaseReturns.AsNoTracking()
            join sup in _db.Suppliers on pr.SupplierId equals sup.Id
            where pr.SupplierId == supplierId && !pr.IsVoided
            select new { pr.Id, pr.OrderNo, sup.Name, pr.RefundAmount, pr.CreatedAt };

        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
        {
            purchaseQ = purchaseQ.Where(x => x.CreatedAt >= df);
            returnQ = returnQ.Where(x => x.CreatedAt >= df);
        }
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
        {
            var end = dt.AddDays(1);
            purchaseQ = purchaseQ.Where(x => x.CreatedAt < end);
            returnQ = returnQ.Where(x => x.CreatedAt < end);
        }

        var purchases = await purchaseQ.ToListAsync();
        var returns = await returnQ.ToListAsync();

        // 退货数量没有冗余列，按单据聚合明细求得（单独一次查询，避免在投影里写相关子查询 ——
        // InMemory 提供程序对相关子查询的支持不稳，写进去单测就会假失败）
        var returnIds = returns.Select(r => r.Id).ToList();
        var returnQty = returnIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.PurchaseReturnDetails.AsNoTracking()
                .Where(d => returnIds.Contains(d.ReturnId))
                .GroupBy(d => d.ReturnId)
                .Select(g => new { ReturnId = g.Key, Qty = g.Sum(x => x.Qty) })
                .ToDictionaryAsync(x => x.ReturnId, x => x.Qty);

        var ledger = purchases
            .Select(p => new LedgerLine("进货", p.OrderNo, p.Name, p.TotalQty, p.TotalAmount, p.CreatedAt))
            .Concat(returns.Select(r => new LedgerLine("退货", r.OrderNo, r.Name,
                returnQty.TryGetValue(r.Id, out var q) ? q : 0m, -r.RefundAmount, r.CreatedAt)))
            .OrderByDescending(x => x.At).ThenByDescending(x => x.OrderNo)
            .ToList();

        var items = ledger.Select(x => (object)new
        {
            type = x.Type, orderNo = x.OrderNo, supplierName = x.SupplierName,
            qty = x.Qty, amount = Math.Round(x.Amount, 2),
            createdAt = x.At.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();

        var purchaseTotal = Math.Round(purchases.Sum(p => p.TotalAmount), 2);
        var returnTotal = Math.Round(returns.Sum(r => r.RefundAmount), 2);

        return new
        {
            items,
            count = items.Count,               // 流水总条数（进货 + 退货）
            purchaseCount = purchases.Count,
            returnCount = returns.Count,
            purchaseTotal,                     // 进货合计（应付增加）
            returnTotal,                       // 退货合计（应付减少，正数表示「退回去的货值多少」）
            netPayable = Math.Round(purchaseTotal - returnTotal, 2),   // 净应付
        };
    }

    /// <summary>对账单流水的一行（进货金额记正、退货记负，合并后按时间倒序排）</summary>
    private readonly record struct LedgerLine(string Type, string OrderNo, string SupplierName,
        decimal Qty, decimal Amount, DateTime At);

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

        // GroupBy(1) 聚合最多返回一行：ToList 让聚合留在 SQL 端，又避免 First 生成无 WHERE 的 TOP(1)
        var stats = (await q.GroupBy(_ => 1).Select(g => new
        {
            TotalCredit = g.Sum(x => x.CreditAmount),
            PaidCredit = g.Sum(x => x.PaidAmount),
            UnpaidCredit = g.Sum(x => x.RemainingAmount),
        }).ToListAsync()).FirstOrDefault();

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

        // 今日（已作废订单不计入销售额/订单数/客单价/毛利）
        var todayOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => !o.IsVoided && o.CreatedAt >= today && o.CreatedAt < tomorrow).ToListAsync();
        var todayIds = todayOrders.Select(o => o.Id).ToList();
        var todayDetails = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => todayIds.Contains(d.OrderId)).ToListAsync();
        var todayReturns = await LoadReturnsAsync(today, tomorrow);
        var todayRefund = Math.Round(todayReturns.Sum(x => x.Refund), 2);
        var todayRefundCost = Math.Round(todayReturns.Sum(x => x.Cost), 2);
        var todaySales = Math.Round(todayOrders.Sum(o => o.PayAmount) - todayRefund, 2);
        var todayReceived = Math.Round(todayOrders.Sum(o => o.ReceivedAmount), 2);
        var todayDiscount = Math.Round(todayOrders.Sum(o => o.DiscountAmount + o.RoundOffAmount), 2);

        // 本月
        var monthOrders = await _db.SaleOrders.AsNoTracking()
            .Where(o => !o.IsVoided && o.CreatedAt >= monthStart && o.CreatedAt < tomorrow)
            .Select(o => new { o.Id, o.PayAmount, o.ReceivedAmount, o.DiscountAmount, o.RoundOffAmount }).ToListAsync();
        var monthIds = monthOrders.Select(o => o.Id).ToList();
        var monthCost = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => monthIds.Contains(d.OrderId)).SumAsync(d => d.Quantity * d.CostPrice);
        var monthList = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => monthIds.Contains(d.OrderId)).SumAsync(d => d.Quantity * d.OriginalPrice);
        var monthReturns = await LoadReturnsAsync(monthStart, tomorrow);
        var monthRefund = Math.Round(monthReturns.Sum(x => x.Refund), 2);
        var monthRefundCost = Math.Round(monthReturns.Sum(x => x.Cost), 2);
        var monthSales = Math.Round(monthOrders.Sum(o => o.PayAmount) - monthRefund, 2);

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
            .Where(o => !o.IsVoided && o.CreatedAt >= trendStart && o.CreatedAt < tomorrow)
            .Select(o => new { o.CreatedAt, o.PayAmount }).ToListAsync();
        var trendReturns = await LoadReturnsAsync(trendStart, tomorrow);
        var trend = Enumerable.Range(0, 7).Select(i =>
        {
            var s = trendStart.AddDays(i);
            var e = s.AddDays(1);
            return new
            {
                date = s.ToString("MM-dd"),
                sales = Math.Round(trendOrders.Where(o => o.CreatedAt >= s && o.CreatedAt < e).Sum(o => o.PayAmount)
                                - trendReturns.Where(r => r.At >= s && r.At < e).Sum(r => r.Refund), 2),
            };
        });

        var monthTopStart = today.AddDays(-30);
        var top5 = await (
            from d in _db.SaleOrderDetails.AsNoTracking()
            join o in _db.SaleOrders on d.OrderId equals o.Id
            where !o.IsVoided && o.CreatedAt >= monthTopStart
            group d by d.ProductName into g
            orderby g.Sum(x => x.SubTotal) descending
            select new { name = g.Key, qty = g.Sum(x => x.Quantity), amount = Math.Round(g.Sum(x => x.SubTotal), 2) }
        ).Take(5).ToListAsync();

        return new
        {
            today = new
            {
                sales = todaySales,
                received = todayReceived,
                refund = todayRefund,
                orders = todayOrders.Count,
                avg = todayOrders.Count == 0 ? 0 : Math.Round(todaySales / todayOrders.Count, 2),
                profit = Math.Round(todayDetails.Sum(d => d.Quantity * d.OriginalPrice - d.Quantity * d.CostPrice)
                                    - todayDiscount - (todayRefund - todayRefundCost), 2),
            },
            month = new
            {
                sales = monthSales,
                received = Math.Round(monthOrders.Sum(o => o.ReceivedAmount), 2),
                refund = monthRefund,
                orders = monthOrders.Count,
                // 收入侧取原价合计（monthSales 已扣过优惠/抹零，再减一次就重复了）
                profit = Math.Round((decimal)monthList - (decimal)monthCost
                                    - monthOrders.Sum(o => o.DiscountAmount + o.RoundOffAmount)
                                    - (monthRefund - monthRefundCost), 2),
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
