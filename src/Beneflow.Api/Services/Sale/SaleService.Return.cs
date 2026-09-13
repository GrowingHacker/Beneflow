using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>销售管理（部分）：销售退货。</summary>
public partial class SaleService : ISaleService
{
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

        // 只读预取原单涉及的商品用于加锁（此步不修改数据）。
        // 退货同时会回补库存并累加明细的 ReturnedQuantity，锁在同一批商品上，
        // 因此「重复整单退」「超量退」的并发请求也会被串行化。
        var orderNo = dto.OriginalOrderNo.Trim();
        var orderId = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.OrderNo == orderNo).Select(o => o.Id).FirstOrDefaultAsync();
        var productIds = orderId == 0
            ? new List<int>()
            : await _db.SaleOrderDetails.AsNoTracking()
                .Where(d => d.OrderId == orderId).Select(d => d.ProductId).Distinct().ToListAsync();

        using (await _stockLock.AcquireOrderNoAsync())
        using (await _stockLock.AcquireAsync(productIds))
        {
            return await CreateReturnCoreAsync(dto);
        }
    }

    /// <summary>销售退货的实际逻辑；调用方须已持有单据号锁与原单相关商品的库存锁。</summary>
    private async Task<ApiResult<object>> CreateReturnCoreAsync(CreateSaleReturnDto dto)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var order = await _db.SaleOrders.FirstOrDefaultAsync(o => o.OrderNo == dto.OriginalOrderNo.Trim());
            if (order == null) return ApiResult<object>.Fail("原销售单不存在");
            if (order.IsVoided) return ApiResult<object>.Fail("该销售单已作废，不能退货");

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
}
