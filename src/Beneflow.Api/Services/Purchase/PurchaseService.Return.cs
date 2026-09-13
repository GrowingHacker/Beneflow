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
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(r => (object)new
        {
            id = r.r.Id, orderNo = r.r.OrderNo, supplierId = r.r.SupplierId, supplierName = r.Name,
            refundAmount = r.r.RefundAmount, reason = r.r.Reason,
            createdAt = r.r.CreatedAt.ToString("yyyy-MM-dd HH:mm"), createdByName = r.UserName,
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
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
}
