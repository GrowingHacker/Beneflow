using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>采购管理（部分）：采购退货。</summary>
public partial class PurchaseService : IPurchaseService
{
    // ================= 采购退货 =================

    public async Task<PagedResult<object>> ReturnListAsync(string? keyword, int page, int pageSize)
    {
        var q =
            from r in _db.PurchaseReturns.AsNoTracking()
            join s in _db.Suppliers on r.SupplierId equals s.Id
            join u in _db.Users on r.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword) || r.OrderNo.Contains(keyword) || s.Name.Contains(keyword)
            orderby r.Id descending
            select new { r, s.Name, UserName = u.Name };

        var total = await q.CountAsync();

        // 合计口径与销售单列表一致：同筛选条件、剔除已作废 —— 作废单在列表里仍可查到，但不参与金额统计，
        // 否则「应退合计」会把已经取消的退货也算进去。
        var sum = (await q.Where(x => !x.r.IsVoided)
            .GroupBy(x => 1)
            .Select(g => new { Returns = g.Count(), RefundAmount = g.Sum(x => x.r.RefundAmount) })
            .ToListAsync()).FirstOrDefault();

        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(r => (object)new
        {
            id = r.r.Id, orderNo = r.r.OrderNo, supplierId = r.r.SupplierId, supplierName = r.Name,
            refundAmount = r.r.RefundAmount, reason = r.r.Reason,
            isVoided = r.r.IsVoided,
            status = r.r.IsVoided ? "已作废" : "已退货",
            createdAt = r.r.CreatedAt.ToString("yyyy-MM-dd HH:mm"), createdByName = r.UserName,
        }).ToList();
        return new PagedResult<object>
        {
            List = list, Total = total, Page = page, PageSize = pageSize,
            Summary = new
            {
                returns = sum?.Returns ?? 0,
                refundAmount = Math.Round(sum?.RefundAmount ?? 0, 2),
            },
        };
    }

    /// <summary>创建采购退货单：扣减库存（不允许退成负数）、写流水、记录应退款项。</summary>
    public async Task<ApiResult<object>> CreateReturnAsync(CreatePurchaseReturnDto dto)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return ApiResult<object>.Fail("请添加退货明细");
        if (dto.Items.Any(i => i.Qty <= 0)) return ApiResult<object>.Fail("退货数量必须大于 0");

        using (await _stockLock.AcquireOrderNoAsync())
        using (await _stockLock.AcquireAsync(dto.Items.Select(i => i.ProductId)))
        {
            return await CreateReturnCoreAsync(dto);
        }
    }

    /// <summary>创建采购退货单的实际逻辑；调用方须已持有单据号锁与相关商品的库存锁。</summary>
    private async Task<ApiResult<object>> CreateReturnCoreAsync(CreatePurchaseReturnDto dto)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var todayCount = await _db.PurchaseReturns.CountAsync(r => r.CreatedAt >= DateTime.Today);
            var orderNo = $"PR{DateTime.Now:yyyyMMdd}{todayCount + 1:D3}";

            var productIds = dto.Items.Select(i => i.ProductId).ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            decimal refundTotal = 0;
            var ret = new PurchaseReturn
            {
                OrderNo = orderNo, SupplierId = dto.SupplierId, Reason = dto.Reason,
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            };
            _db.PurchaseReturns.Add(ret);
            await _db.SaveChangesAsync();

            foreach (var item in dto.Items.GroupBy(i => i.ProductId).Select(g =>
                     new PurchaseReturnItemDto { ProductId = g.Key, Qty = g.Sum(x => x.Qty), CostPrice = g.First().CostPrice }))
            {
                if (!products.TryGetValue(item.ProductId, out var p))
                    return ApiResult<object>.Fail($"商品 ID {item.ProductId} 不存在");

                // 成本价以当前账面成本为准（退货按现价退给供应商）
                var price = item.CostPrice > 0 ? item.CostPrice : p.CostPrice;
                if (p.StockQuantity < item.Qty)
                    return ApiResult<object>.Fail($"商品「{p.Name}」库存不足（当前 {p.StockQuantity}）");

                var before = p.StockQuantity;
                p.StockQuantity -= item.Qty;
                p.UpdatedAt = DateTime.Now;
                refundTotal += item.Qty * price;

                _db.PurchaseReturnDetails.Add(new PurchaseReturnDetail
                {
                    ReturnId = ret.Id, ProductId = p.Id, Qty = item.Qty,
                    CostPrice = price, SubTotal = Math.Round(item.Qty * price, 2),
                });
                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id, ChangeType = "采购退货出库", ChangeQty = -item.Qty,
                    BeforeQty = before, AfterQty = p.StockQuantity,
                    RefNo = orderNo, CreatedBy = _me.Id, CreatedAt = ret.CreatedAt,
                });
            }

            ret.RefundAmount = Math.Round(refundTotal, 2);
            await _db.SaveChangesAsync();
            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "采购管理",
                Action = "创建采购退货单", Target = $"{orderNo} 应退 ¥{ret.RefundAmount}",
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult<object>.Ok(new { id = ret.Id, orderNo, refundTotal = ret.RefundAmount });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("创建采购退货单失败：" + ex.Message, ex);
        }
    }

    /// <summary>作废采购退货单：回补库存、写流水、标记作废（照 <see cref="VoidAsync"/> 的既有写法）。</summary>
    public async Task<ApiResult> VoidReturnAsync(int id)
    {
        // 先只读地取出涉及的商品用于加锁（此步不修改任何数据），
        // 「是否已作废」的判定留在锁内，同一退货单的并发作废会被串行化，不会重复回补库存。
        var productIds = await _db.PurchaseReturnDetails.AsNoTracking()
            .Where(d => d.ReturnId == id).Select(d => d.ProductId).Distinct().ToListAsync();

        using (await _stockLock.AcquireAsync(productIds))
        {
            return await VoidReturnCoreAsync(id);
        }
    }

    /// <summary>
    /// 作废采购退货单的实际逻辑；调用方须已持有相关商品的库存锁。
    ///
    /// 只回补数量、**不动成本价**：退货那一刻 `CreateReturnCoreAsync` 也只改了 StockQuantity
    /// （没有重算移动加权成本），所以把数量加回去就完全还原了，成本价本来就没被这次退货碰过。
    /// </summary>
    private async Task<ApiResult> VoidReturnCoreAsync(int id)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var ret = await _db.PurchaseReturns.FirstOrDefaultAsync(r => r.Id == id);
            if (ret == null) return ApiResult.Fail("采购退货单不存在");
            if (ret.IsVoided) return ApiResult.Fail("该退货单已作废，无需重复操作");

            var details = await _db.PurchaseReturnDetails.Where(d => d.ReturnId == id).ToListAsync();
            if (details.Count == 0) return ApiResult.Fail("退货单缺少明细，无法作废");

            var productIds = details.Select(d => d.ProductId).Distinct().ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            foreach (var d in details)
            {
                if (!products.TryGetValue(d.ProductId, out var p))
                    return ApiResult.Fail($"商品记录已被删除（ID {d.ProductId}），无法回补库存");

                var before = p.StockQuantity;
                p.StockQuantity += d.Qty;   // 退货是出库，作废即把当初减掉的加回来
                p.UpdatedAt = DateTime.Now;

                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id, ChangeType = "采购退货作废回补", ChangeQty = d.Qty,
                    BeforeQty = before, AfterQty = p.StockQuantity,
                    RefNo = ret.OrderNo, CreatedBy = _me.Id, CreatedAt = DateTime.Now,
                });
            }

            ret.IsVoided = true;
            ret.VoidedAt = DateTime.Now;

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp,
                Module = "采购管理",
                Action = "作废采购退货单",
                Target = $"{ret.OrderNo} 应退 ¥{ret.RefundAmount}，回补库存 {details.Sum(d => d.Qty)} 件",
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult.Ok();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("作废采购退货单失败：" + ex.Message, ex);
        }
    }
}
