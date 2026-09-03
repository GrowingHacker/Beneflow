using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>销售管理：收银结算 + 赊账 + 销售退货</summary>
public class SaleService : ISaleService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;

    public SaleService(AppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    // ================= 销售单 =================

    public async Task<PagedResult<object>> ListAsync(string? keyword, string? dateFrom, string? dateTo,
        string? payMethod, int page, int pageSize)
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

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.OrderByDescending(x => x.o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        // 退货状态由明细退货数量推导（按当前页订单聚合）
        var ids = rows.Select(r => r.o.Id).ToList();
        var retAgg = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => ids.Contains(d.OrderId))
            .GroupBy(d => d.OrderId)
            .Select(g => new { OrderId = g.Key, Qty = g.Sum(x => x.Quantity), Ret = g.Sum(x => x.ReturnedQuantity) })
            .ToDictionaryAsync(x => x.OrderId, x => x);

        var list = rows.Select(r =>
        {
            retAgg.TryGetValue(r.o.Id, out var agg);
            return (object)new
            {
                id = r.o.Id, orderNo = r.o.OrderNo,
                totalAmount = r.o.TotalAmount, discountAmount = r.o.DiscountAmount,
                payAmount = r.o.PayAmount, payMethod = r.o.PayMethod,
                cashAmount = r.o.CashAmount, changeAmount = r.o.ChangeAmount,
                status = StatusText(agg?.Qty ?? 0, agg?.Ret ?? 0),
                isCredit = r.o.IsCredit, wechatId = r.o.WechatId,
                createdAt = r.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                createdByName = r.UserName,
            };
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    public async Task<ApiResult<object>> GetDetailAsync(int id)
    {
        var order = await _db.SaleOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return ApiResult<object>.Fail("销售单不存在");
        return ApiResult<object>.Ok(await ShapeDetailAsync(order));
    }

    public async Task<ApiResult<object>> GetDetailByOrderNoAsync(string orderNo)
    {
        var order = await _db.SaleOrders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderNo == orderNo);
        if (order == null) return ApiResult<object>.Fail("销售单不存在");
        return ApiResult<object>.Ok(await ShapeDetailAsync(order));
    }

    private async Task<object> ShapeDetailAsync(SaleOrder o)
    {
        var userName = await _db.Users.Where(u => u.Id == o.CreatedBy).Select(u => u.Name).FirstOrDefaultAsync();
        var details = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => d.OrderId == o.Id)
            .Select(d => new
            {
                d.Id, barcode = d.Barcode, name = d.ProductName,
                qty = d.Quantity, unitPrice = d.UnitPrice,
                returnedQty = d.ReturnedQuantity, subTotal = d.SubTotal,
            }).ToListAsync();
        return new
        {
            id = o.Id, orderNo = o.OrderNo,
            totalAmount = o.TotalAmount, discountAmount = o.DiscountAmount,
            payAmount = o.PayAmount, payMethod = o.PayMethod,
            cashAmount = o.CashAmount, changeAmount = o.ChangeAmount,
            status = StatusText(details.Sum(d => d.qty), details.Sum(d => d.returnedQty)),
            isCredit = o.IsCredit, wechatId = o.WechatId,
            createdAt = o.CreatedAt.ToString("yyyy-MM-dd HH:mm"), createdByName = userName ?? "",
            items = details,
        };
    }

    /// <summary>由明细退货数量推导销售单状态：未退=已完成 / 全退=已退货 / 部分退=部分退货</summary>
    private static string StatusText(decimal qty, decimal returned) =>
        returned <= 0 ? "已完成" : returned >= qty ? "已退货" : "部分退货";

    /// <summary>
    /// 收银结算：校验库存 → 扣库存（快照成本价）→ 写流水；赊账同时生成 CreditSale 欠款记录。
    /// </summary>
    public async Task<ApiResult<object>> CreateAsync(CreateSaleDto dto)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return ApiResult<object>.Fail("购物清单为空");
        if (dto.PayMethod == "赊账" && !dto.IsCredit)
            dto.IsCredit = true;

        if (dto.Items.Any(i => i.Qty <= 0)) return ApiResult<object>.Fail("商品数量必须大于 0");

        // 系统设置是否允许赊账
        var allowCredit = await _db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == "sale").Select(c => c.ConfigValue).FirstOrDefaultAsync();
        if (dto.IsCredit && allowCredit != null && allowCredit.Contains("\"allowCredit\":false"))
            return ApiResult<object>.Fail("系统设置不允许赊账，请更换收款方式");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var todayCount = await _db.SaleOrders.CountAsync(o => o.CreatedAt >= DateTime.Today);
            var orderNo = $"SO{DateTime.Now:yyyyMMdd}{todayCount + 1:D3}";

            var productIds = dto.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            foreach (var item in dto.Items.GroupBy(i => i.ProductId).Select(g =>
                     new SaleItemDto { ProductId = g.Key, Qty = g.Sum(x => x.Qty) }))
            {
                if (!products.TryGetValue(item.ProductId, out var p))
                    return ApiResult<object>.Fail($"商品 ID {item.ProductId} 不存在");
                if (p.StockQuantity < item.Qty)
                    return ApiResult<object>.Fail($"商品「{p.Name}」库存不足（当前 {p.StockQuantity}）");
            }

            var so = new SaleOrder
            {
                OrderNo = orderNo,
                TotalAmount = Math.Round(dto.TotalAmount, 2),
                DiscountAmount = Math.Round(dto.DiscountAmount, 2),
                PayAmount = Math.Round(dto.PayAmount, 2),
                PayMethod = dto.PayMethod,
                CashAmount = Math.Round(dto.CashAmount, 2),
                ChangeAmount = Math.Round(dto.ChangeAmount, 2),
                IsCredit = dto.IsCredit,
                WechatId = dto.IsCredit ? (string.IsNullOrWhiteSpace(dto.WechatId) ? null : dto.WechatId!.Trim()) : null,
                Remark = dto.Remark,
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            };
            _db.SaleOrders.Add(so);
            await _db.SaveChangesAsync();

            foreach (var item in dto.Items)
            {
                var p = products[item.ProductId];
                var before = p.StockQuantity;
                p.StockQuantity -= item.Qty;
                p.UpdatedAt = DateTime.Now;

                _db.SaleOrderDetails.Add(new SaleOrderDetail
                {
                    OrderId = so.Id, ProductId = p.Id,
                    ProductName = string.IsNullOrWhiteSpace(item.Name) ? p.Name : item.Name!,
                    Barcode = item.Barcode ?? p.Barcode,
                    Quantity = item.Qty,
                    UnitPrice = item.UnitPrice > 0 ? item.UnitPrice : p.SalePrice,
                    CostPrice = p.CostPrice,   // 销售时成本快照，毛利核算依据
                    SubTotal = Math.Round(item.Qty * (item.UnitPrice > 0 ? item.UnitPrice : p.SalePrice), 2),
                });
                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id, ChangeType = "销售出库", ChangeQty = -item.Qty,
                    BeforeQty = before, AfterQty = p.StockQuantity,
                    RefNo = orderNo, CreatedBy = _me.Id, CreatedAt = so.CreatedAt,
                });
            }

            // 赊账欠款记录
            if (dto.IsCredit)
            {
                _db.CreditSales.Add(new CreditSale
                {
                    SaleOrderId = so.Id, WechatId = string.IsNullOrWhiteSpace(dto.WechatId) ? "" : dto.WechatId!.Trim(),
                    Phone = string.IsNullOrWhiteSpace(dto.Phone) ? null : dto.Phone!.Trim(),
                    Remark = string.IsNullOrWhiteSpace(dto.Remark) ? null : dto.Remark!.Trim(),
                    CreditAmount = so.PayAmount, PaidAmount = 0, RemainingAmount = so.PayAmount,
                    Status = false, CreatedAt = so.CreatedAt,
                });
            }

            await _db.SaveChangesAsync();
            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "销售管理",
                Action = "收银结算", Target = $"{orderNo} 实收 ¥{so.PayAmount} ({so.PayMethod})",
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult<object>.Ok(new { id = so.Id, orderNo });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("创建销售单失败：" + ex.Message, ex);
        }
    }

    // ================= 销售退货 =================

    public async Task<PagedResult<object>> ReturnListAsync(string? keyword, int page, int pageSize)
    {
        var q =
            from r in _db.SaleReturns.AsNoTracking()
            join u in _db.Users on r.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword) || r.OrderNo.Contains(keyword)
            orderby r.Id descending
            select new { r, UserName = u.Name };

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(r => (object)new
        {
            id = r.r.Id, orderNo = r.r.OrderNo, saleOrderNo =
                _db.SaleOrders.Where(s => s.Id == r.r.SaleOrderId).Select(s => s.OrderNo).FirstOrDefault(),
            refundMethod = r.r.RefundMethod, refundAmount = r.r.RefundAmount,
            createdAt = r.r.CreatedAt.ToString("yyyy-MM-dd HH:mm"), createdByName = r.UserName,
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>
    /// 创建销售退货单：按原销售单明细核销可退数量 → 回补库存 → 写流水。
    /// </summary>
    public async Task<ApiResult<object>> CreateReturnAsync(CreateSaleReturnDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.OriginalOrderNo))
            return ApiResult<object>.Fail("请输入原销售单号");
        if (dto.Items == null || dto.Items.Count == 0)
            return ApiResult<object>.Fail("请选择退货商品");
        if (dto.Items.Any(i => i.Qty <= 0)) return ApiResult<object>.Fail("退货数量必须大于 0");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var order = await _db.SaleOrders.FirstOrDefaultAsync(o => o.OrderNo == dto.OriginalOrderNo.Trim());
            if (order == null) return ApiResult<object>.Fail("原销售单不存在");

            var details = await _db.SaleOrderDetails
                .Where(d => d.OrderId == order.Id).ToListAsync();
            if (details.Count == 0) return ApiResult<object>.Fail("原销售单缺少明细");
            if (AllReturned(details)) return ApiResult<object>.Fail("该销售单已整单退货，不能重复退");

            var todayCount = await _db.SaleReturns.CountAsync(r => r.CreatedAt >= DateTime.Today);
            var returnNo = $"SR{DateTime.Now:yyyyMMdd}{todayCount + 1:D3}";

            var ret = new SaleReturn
            {
                OrderNo = returnNo, SaleOrderId = order.Id, RefundMethod = dto.RefundMethod,
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            };
            _db.SaleReturns.Add(ret);
            await _db.SaveChangesAsync();

            decimal refundTotal = 0;
            foreach (var item in dto.Items)
            {
                // 优先按明细 ID 匹配，其次按 商品名称+单价 匹配（前端传的是名称）
                SaleOrderDetail detail;
                if (item.DetailId.HasValue)
                {
                    var d = details.FirstOrDefault(x => x.Id == item.DetailId.Value);
                    if (d == null) return ApiResult<object>.Fail($"销售明细 {item.DetailId} 不属于该订单");
                    detail = d;
                }
                else
                {
                    var cands = details.Where(x =>
                        (x.ProductName == item.Name || x.Barcode == item.Name) &&
                        (item.UnitPrice <= 0 || x.UnitPrice == item.UnitPrice)).ToList();
                    if (cands.Count == 0) return ApiResult<object>.Fail($"原单未找到商品「{item.Name}」");
                    detail = cands.OrderBy(x => x.Quantity - x.ReturnedQuantity).First(); // 先核销剩余最多的？→ 剩余可退的
                }

                var remaining = detail.Quantity - detail.ReturnedQuantity;
                if (remaining <= 0)
                    return ApiResult<object>.Fail($"商品「{detail.ProductName}」已无可退数量");
                if (item.Qty > remaining)
                    return ApiResult<object>.Fail($"商品「{detail.ProductName}」可退数量仅剩 {remaining}");

                var p = await _db.Products.FindAsync(detail.ProductId);
                if (p == null) return ApiResult<object>.Fail($"商品「{detail.ProductName}」已被删除，无法回补库存");

                var before = p.StockQuantity;
                p.StockQuantity += item.Qty;
                p.UpdatedAt = DateTime.Now;
                detail.ReturnedQuantity += item.Qty;

                refundTotal += item.Qty * detail.UnitPrice;
                _db.SaleReturnDetails.Add(new SaleReturnDetail
                {
                    ReturnId = ret.Id, SaleOrderDetailId = detail.Id, ProductId = detail.ProductId,
                    ProductName = detail.ProductName, Qty = item.Qty, UnitPrice = detail.UnitPrice,
                    SubTotal = Math.Round(item.Qty * detail.UnitPrice, 2),
                });
                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id, ChangeType = "销售退货入库", ChangeQty = item.Qty,
                    BeforeQty = before, AfterQty = p.StockQuantity,
                    RefNo = returnNo, CreatedBy = _me.Id, CreatedAt = ret.CreatedAt,
                });
            }

            ret.RefundAmount = Math.Round(refundTotal, 2);

            // 赊账退款冲抵欠款
            if (order.IsCredit)
            {
                var credit = await _db.CreditSales.FirstOrDefaultAsync(c => c.SaleOrderId == order.Id && !c.Status);
                if (credit != null)
                {
                    credit.PaidAmount += ret.RefundAmount;
                    credit.RemainingAmount = Math.Max(0, credit.CreditAmount - credit.PaidAmount);
                    credit.Status = credit.RemainingAmount <= 0;
                    if (credit.Status) credit.SettledAt = DateTime.Now;
                }
            }

            await _db.SaveChangesAsync();
            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "销售管理",
                Action = "创建销售退货单", Target = $"{returnNo} 原 {order.OrderNo} 退 ¥{ret.RefundAmount}",
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult<object>.Ok(new { id = ret.Id, orderNo = returnNo, refundTotal = ret.RefundAmount });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("创建销售退货单失败：" + ex.Message, ex);
        }
    }

    private static bool AllReturned(List<SaleOrderDetail> details) =>
        details.All(d => d.ReturnedQuantity >= d.Quantity);

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

    public async Task<ApiResult> SettleAsync(int id, SettleCreditDto dto, string ip)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var c = await _db.CreditSales.FirstOrDefaultAsync(x => x.Id == id);
            if (c == null) return ApiResult.Fail("赊账记录不存在");
            if (c.Status) return ApiResult.Fail("该笔欠款已结清");
            if (c.RemainingAmount <= 0) return ApiResult.Fail("无待还金额");

            _db.CreditPayments.Add(new CreditPayment
            {
                CreditSaleId = c.Id, PayAmount = c.RemainingAmount,
                PayMethod = string.IsNullOrEmpty(dto.PayMethod) ? "微信" : dto.PayMethod,
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            });

            c.PaidAmount += c.RemainingAmount;
            c.RemainingAmount = 0;
            c.Status = true;
            c.SettledAt = DateTime.Now;

            await _db.SaveChangesAsync();
            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "赊账管理",
                Action = "结清欠款", Target = $"记录 #{id} 微信号 {c.WechatId}",
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult.Ok();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("结清失败：" + ex.Message, ex);
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
