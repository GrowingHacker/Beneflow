using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Beneflow.Api.Services;

/// <summary>库存管理（部分）：盘点单。</summary>
public partial class StockService : IStockService
{
    // ================= 盘点单 =================

    public async Task<PagedResult<object>> CheckListAsync(int page, int pageSize)
    {
        var q =
            from sc in _db.StockChecks.AsNoTracking()
            join u in _db.Users on sc.CreatedBy equals u.Id
            orderby sc.Id descending
            select new { sc, UserName = u.Name };
        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var list = rows.Select(r => (object)new
        {
            id = r.sc.Id, orderNo = r.sc.OrderNo, range = r.sc.Range,
            itemCount = r.sc.ItemCount, profit = r.sc.ProfitQty, loss = r.sc.LossQty,
            status = r.sc.Status ? "已确认" : "草稿",
            createdAt = r.sc.CreatedAt.ToString("yyyy-MM-dd HH:mm"), createdByName = r.UserName,
            confirmedAt = r.sc.ConfirmedAt?.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    public async Task<ApiResult<object>> CreateCheckAsync(CreateStockCheckDto dto)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return ApiResult<object>.Fail("盘点明细为空");

        // 建盘点单只分配单号、不动库存，因此只需单据号锁
        using (await _stockLock.AcquireOrderNoAsync())
        {
            return await CreateCheckCoreAsync(dto);
        }
    }

    /// <summary>创建盘点单的实际逻辑；调用方须已持有单据号锁。</summary>
    private async Task<ApiResult<object>> CreateCheckCoreAsync(CreateStockCheckDto dto)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var todayCount = await _db.StockChecks.CountAsync(c => c.CreatedAt >= DateTime.Today);
            var orderNo = $"SC{DateTime.Now:yyyyMMdd}{todayCount + 1:D3}";

            var check = new StockCheck
            {
                OrderNo = orderNo, Range = dto.Range ?? "全部商品",
                Status = dto.Status == "已确认",
                ItemCount = dto.Items.Count,
                ProfitQty = dto.Items.Where(i => i.ActualQty > i.BookQty).Sum(i => i.ActualQty - i.BookQty),
                LossQty = dto.Items.Where(i => i.ActualQty < i.BookQty).Sum(i => i.BookQty - i.ActualQty),
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            };
            _db.StockChecks.Add(check);
            await _db.SaveChangesAsync();

            foreach (var i in dto.Items)
            {
                _db.StockCheckDetails.Add(new StockCheckDetail
                {
                    CheckId = check.Id, ProductId = i.Id,
                    BookQty = i.BookQty, ActualQty = i.ActualQty, DiffQty = i.ActualQty - i.BookQty,
                });
            }
            await _db.SaveChangesAsync();

            // 直接提交确认的：立即调整库存
            if (check.Status)
                await ApplyCheckAsyncInternal(check.Id);

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "库存管理",
                Action = "创建盘点单", Target = orderNo,
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult<object>.Ok(new { id = check.Id, orderNo });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("创建盘点单失败：" + ex.Message, ex);
        }
    }

    /// <summary>确认盘点：按差异调整库存并写「盘点调整」流水。</summary>
    public async Task<ApiResult> ConfirmCheckAsync(int id)
    {
        // 只读预取涉及的商品用于加锁（此步不修改数据）。
        // 「是否已确认」判定留在锁内，因此同一盘点单的并发确认会被串行化，
        // 不会因为两次都通过校验而把库存重复调整。
        var productIds = await _db.StockCheckDetails.AsNoTracking()
            .Where(d => d.CheckId == id).Select(d => d.ProductId).Distinct().ToListAsync();

        using (await _stockLock.AcquireAsync(productIds))
        {
            return await ConfirmCheckCoreAsync(id);
        }
    }

    /// <summary>确认盘点单的实际逻辑；调用方须已持有相关商品的库存锁。</summary>
    private async Task<ApiResult> ConfirmCheckCoreAsync(int id)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var check = await _db.StockChecks.FirstOrDefaultAsync(c => c.Id == id);
            if (check == null) return ApiResult.Fail("盘点单不存在");
            if (check.Status) return ApiResult.Fail("该盘点单已确认过");

            await ApplyCheckAsyncInternal(id);
            check.Status = true;
            check.ConfirmedAt = DateTime.Now;

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "库存管理",
                Action = "确认盘点", Target = check.OrderNo,
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult.Ok();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("确认盘点失败：" + ex.Message, ex);
        }
    }

    private async Task ApplyCheckAsyncInternal(int checkId)
    {
        var details = await _db.StockCheckDetails.Where(d => d.CheckId == checkId).ToListAsync();
        var check = await _db.StockChecks.FindAsync(checkId);
        foreach (var d in details.Where(d => d.DiffQty != 0))
        {
            var p = await _db.Products.FindAsync(d.ProductId);
            if (p == null) continue;
            var before = p.StockQuantity;
            p.StockQuantity = d.ActualQty;
            p.UpdatedAt = DateTime.Now;
            _db.StockLogs.Add(new StockLog
            {
                ProductId = p.Id, ChangeType = "盘点调整", ChangeQty = d.DiffQty,
                BeforeQty = before, AfterQty = d.ActualQty,
                RefNo = check?.OrderNo ?? "", CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            });
        }
        await _db.SaveChangesAsync();
    }
}
