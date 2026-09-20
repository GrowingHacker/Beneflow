using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>销售管理（部分）：赊账（列表 / 导出 / 还款 / 编辑）。</summary>
public partial class SaleService : ISaleService
{
    // ================= 赊账 =================

    /// <summary>赊账列表 + 全量统计（总欠款、未结清笔数、已结清笔数）</summary>
    public async Task<object> CreditListAsync(string? keyword, string? status, string? dateFrom, string? dateTo, int page, int pageSize)
    {
        // 兼容旧字符串参数：未结清/已结清 → bool（true=已结清）
        bool? settled = status switch { "已结清" => true, "未结清" => false, _ => (bool?)null };

        var filtered =
            from c in _db.CreditSales.AsNoTracking()
            join o in _db.SaleOrders on c.SaleOrderId equals o.Id
            where string.IsNullOrEmpty(keyword)
                  || c.WechatId.Contains(keyword)
                  || (c.Phone != null && c.Phone.Contains(keyword))
                  || (c.Remark != null && c.Remark.Contains(keyword))
                  || o.OrderNo.Contains(keyword)
            orderby c.Id descending
            select new { c, saleOrderNo = o.OrderNo };
        if (settled != null)
            filtered = filtered.Where(x => x.c.Status == settled);
        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            filtered = filtered.Where(x => x.c.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            filtered = filtered.Where(x => x.c.CreatedAt < dt.AddDays(1));

        var total = await filtered.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await filtered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(r => (object)new
        {
            id = r.c.Id, saleOrderNo = r.saleOrderNo, wechatId = r.c.WechatId,
            phone = r.c.Phone, remark = r.c.Remark,
            creditAmount = r.c.CreditAmount, paidAmount = r.c.PaidAmount,
            remainingAmount = r.c.RemainingAmount,
            status = r.c.Status ? "已结清" : "未结清",
            settledAt = r.c.SettledAt?.ToString("yyyy-MM-dd HH:mm"),
            createdAt = r.c.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();

        // 统计为全量口径（跨分页，与列表同过滤条件）
        var statsQ =
            from c in _db.CreditSales.AsNoTracking()
            join o in _db.SaleOrders on c.SaleOrderId equals o.Id
            where string.IsNullOrEmpty(keyword)
                  || c.WechatId.Contains(keyword)
                  || (c.Phone != null && c.Phone.Contains(keyword))
                  || (c.Remark != null && c.Remark.Contains(keyword))
                  || o.OrderNo.Contains(keyword)
            where settled == null || c.Status == settled
            where string.IsNullOrEmpty(dateFrom) || c.CreatedAt >= DateTime.Parse(dateFrom)
            where string.IsNullOrEmpty(dateTo) || c.CreatedAt < DateTime.Parse(dateTo).AddDays(1)
            group c by 1 into g
            select new
            {
                TotalCredit = g.Sum(x => x.CreditAmount),
                UnpaidTotal = g.Sum(x => x.RemainingAmount),
                UnsettledCount = g.Count(x => !x.Status),
                SettledCount = g.Count(x => x.Status),
            };
        var s = await statsQ.FirstOrDefaultAsync();
        var stats = new
        {
            totalCredit = Math.Round(s?.TotalCredit ?? 0, 2),
            unpaidTotal = Math.Round(s?.UnpaidTotal ?? 0, 2),
            unsettled = s?.UnsettledCount ?? 0,
            settled = s?.SettledCount ?? 0,
        };

        return new { list, stats, total, page, pageSize };
    }

    /// <summary>导出赊账记录全量（按 keyword/status/dateFrom/dateTo 筛选，不分页），返回行字典</summary>
    public async Task<List<Dictionary<string, object?>>> ExportCreditAsync(string? keyword, string? status, string? dateFrom, string? dateTo)
    {
        bool? settled = status switch { "已结清" => true, "未结清" => false, _ => (bool?)null };

        var filtered =
            from c in _db.CreditSales.AsNoTracking()
            join o in _db.SaleOrders on c.SaleOrderId equals o.Id
            where string.IsNullOrEmpty(keyword)
                  || c.WechatId.Contains(keyword)
                  || (c.Phone != null && c.Phone.Contains(keyword))
                  || (c.Remark != null && c.Remark.Contains(keyword))
                  || o.OrderNo.Contains(keyword)
            orderby c.Id descending
            select new { c, saleOrderNo = o.OrderNo };
        if (settled != null) filtered = filtered.Where(x => x.c.Status == settled);
        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            filtered = filtered.Where(x => x.c.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            filtered = filtered.Where(x => x.c.CreatedAt < dt.AddDays(1));

        var rows = await filtered.ToListAsync();
        return rows.Select(r => new Dictionary<string, object?>
        {
            ["wechatId"] = r.c.WechatId,
            ["phone"] = r.c.Phone,
            ["saleOrderNo"] = r.saleOrderNo,
            ["creditAmount"] = r.c.CreditAmount,
            ["paidAmount"] = r.c.PaidAmount,
            ["remainingAmount"] = r.c.RemainingAmount,
            ["status"] = r.c.Status ? "已结清" : "未结清",
            ["remark"] = r.c.Remark,
            ["createdAt"] = r.c.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();
    }

    /// <summary>
    /// 赊账还款：支持部分还款和全额结清。
    /// - dto.PayAmount &gt; 0：部分还款，金额不能超过剩余欠款
    /// - dto.PayAmount = 0 或不传：全额结清（兼容旧版调用）
    /// </summary>
    public async Task<ApiResult> SettleAsync(int id, SettleCreditDto dto, string ip)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var c = await _db.CreditSales.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return ApiResult.Fail("赊账记录不存在");
            if (c.Status) return ApiResult.Fail("该笔欠款已结清");
            if (c.RemainingAmount <= 0) return ApiResult.Fail("无待还金额");

            // 还款金额：0 或不传则全额结清
            var payAmount = dto.PayAmount > 0 ? Math.Round(dto.PayAmount, 2) : c.RemainingAmount;
            if (payAmount <= 0) return ApiResult.Fail("还款金额必须大于 0");
            if (payAmount > c.RemainingAmount)
                return ApiResult.Fail($"还款金额不能超过剩余欠款（剩余 ¥{c.RemainingAmount}）");

            var payMethod = string.IsNullOrEmpty(dto.PayMethod) ? "微信" : dto.PayMethod;

            _db.CreditPayments.Add(new CreditPayment
            {
                CreditSaleId = c.Id,
                PayAmount = payAmount,
                PayMethod = payMethod,
                CreatedBy = _me.Id,
                CreatedAt = DateTime.Now,
            });

            c.PaidAmount += payAmount;
            c.RemainingAmount = Math.Round(c.RemainingAmount - payAmount, 2);

            var isFullSettle = c.RemainingAmount <= 0;
            if (isFullSettle)
            {
                c.RemainingAmount = 0;
                c.Status = true;
                c.SettledAt = DateTime.Now;
            }

            // 赊账单的「实收」= 实际收到的净额：开单时记 0（钱没到手），之后每笔还款累加。
            // 这样销售单列表 / 报表里的实收会随还款逐步补齐，还清时正好等于真正收回的钱
            // （退货抵欠款的那部分钱没到手，不算实收，故这里只累加还款额）。
            var saleOrder = await _db.SaleOrders.FirstOrDefaultAsync(o => o.Id == c.SaleOrderId);
            if (saleOrder != null)
                saleOrder.ReceivedAmount = Math.Round(saleOrder.ReceivedAmount + payAmount, 2);

            await _db.SaveChangesAsync();

            var action = isFullSettle ? "结清欠款" : "部分还款";
            var target = $"记录 #{id} 微信号 {c.WechatId}，本次还款 ¥{payAmount}（{payMethod}）";
            if (saleOrder != null) target += $"，单据 {saleOrder.OrderNo} 实收更新为 ¥{saleOrder.ReceivedAmount}";
            if (isFullSettle) target += "，已全部结清";

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "赊账管理",
                Action = action, Target = target,
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult.Ok();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("还款失败：" + ex.Message, ex);
        }
    }

    /// <summary>更新赊账记录的手机号和备注</summary>
    public async Task<ApiResult> UpdateCreditAsync(int id, UpdateCreditDto dto)
    {
        var c = await _db.CreditSales.FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return ApiResult.Fail("赊账记录不存在");

        c.Phone = string.IsNullOrWhiteSpace(dto.Phone) ? null : dto.Phone!.Trim();
        c.Remark = string.IsNullOrWhiteSpace(dto.Remark) ? null : dto.Remark!.Trim();

        await _db.SaveChangesAsync();
        _db.OperationLogs.Add(new OperationLog
        {
            UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "赊账管理",
            Action = "修改赊账信息", Target = $"记录 #{id} 微信号 {c.WechatId}",
        });
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }
}
