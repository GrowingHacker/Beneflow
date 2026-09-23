using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Beneflow.Api.Services;

/// <summary>库存管理（部分）：临期商品。</summary>
public partial class StockService : IStockService
{
    // ================= 临期商品 =================

    public async Task<object> ExpiryListAsync(string? tag, int page, int pageSize)
    {
        // tag: 7 天内 / 30 天内 / 已过期 / 已处理
        var processedOnly = tag == "processed";
        var rows = await (
            from b in _db.Batches.AsNoTracking()
            join p in _db.Products on b.ProductId equals p.Id
            where !p.IsDeleted && (!processedOnly || b.IsProcessed)
            orderby b.ExpireDate
            select new { b, p.Name, p.Barcode, p.Unit }
        ).ToListAsync();

        var filtered = rows.AsEnumerable().Where(r =>
        {
            if (processedOnly) return r.b.IsProcessed;
            if (r.b.IsProcessed) return false; // 未处理页不显示已处理
            var left = (int)(r.b.ExpireDate.Date - DateTime.Today).TotalDays;
            return tag switch
            {
                "7" => left >= 0 && left <= 7,
                "30" => left >= 0 && left <= 30,
                "expired" => left < 0,
                _ => true,
            };
        }).ToList();

        var total = filtered.Count;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 500);
        page = Math.Max(1, page);
        var list = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(r => (object)new
        {
            id = r.b.Id, productId = r.b.ProductId, barcode = r.Barcode, name = r.Name, unit = r.Unit,
            batchNo = r.b.BatchNo, quantity = r.b.Quantity,
            expireDate = r.b.ExpireDate.ToString("yyyy-MM-dd"),
            daysLeft = (int)(r.b.ExpireDate.Date - DateTime.Today).TotalDays,
            status = (r.b.ExpireDate.Date - DateTime.Today).TotalDays < 0 ? "已过期"
                   : ((r.b.ExpireDate.Date - DateTime.Today).TotalDays <= 7 ? "临期(7天内)" : "临期(30天内)"),
            isProcessed = r.b.IsProcessed,
            processedAt = r.b.ProcessedAt?.ToString("yyyy-MM-dd HH:mm"),
        }).ToList();
        return new { list, total, page, pageSize };
    }

    /// <summary>
    /// 批量标记临期商品为已处理：扣减对应批次库存，写入库存流水（类型：临期报损）。
    /// 扣减数量以批次剩余数量为准（即批次 Quantity 字段），最多扣减到 0，不会出现负库存。
    /// </summary>
    public async Task<ApiResult<string>> MarkProcessedAsync(long[] ids)
    {
        if (ids == null || ids.Length == 0) return ApiResult<string>.Fail("请选择要标记的记录");

        // 只读预取批次对应的商品用于加锁（此步不修改数据）
        var productIds = await _db.Batches.AsNoTracking()
            .Where(b => ids.Contains(b.Id)).Select(b => b.ProductId).Distinct().ToListAsync();

        using (await _stockLock.AcquireAsync(productIds))
        {
            return await MarkProcessedCoreAsync(ids);
        }
    }

    /// <summary>
    /// 标记临期处理的实际逻辑；调用方须已持有相关商品的库存锁。
    /// 成功时 data 是一句给人看的处理结果（当前前端未使用它，只 toast 固定文案）。
    /// 标成 <c>ApiResult&lt;string&gt;</c> 而不是非泛型包络，是为了 Swagger 里能看出 data 的形状 ——
    /// 非泛型 <see cref="ApiResult"/> 的 data 是 <c>object</c>，在文档里只会显示成空对象 <c>{}</c>。
    /// </summary>
    private async Task<ApiResult<string>> MarkProcessedCoreAsync(long[] ids)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var batches = await _db.Batches
                .Where(b => ids.Contains(b.Id) && !b.IsProcessed)
                .Include(b => b.Product)
                .ToListAsync();

            if (batches.Count == 0) return ApiResult<string>.Fail("所选批次不存在或均已处理");

            var totalQty = 0m;
            var productNames = new List<string>();

            foreach (var b in batches)
            {
                if (b.Quantity <= 0)
                {
                    // 批次数量为 0，直接标记不扣库存
                    b.IsProcessed = true;
                    b.ProcessedAt = DateTime.Now;
                    continue;
                }

                var p = b.Product;
                var beforeQty = p.StockQuantity;
                // 扣减数量：以批次数量为准，但不超过当前库存（避免负库存）
                var deductQty = Math.Min(b.Quantity, p.StockQuantity);
                if (deductQty < 0) deductQty = 0;

                p.StockQuantity -= deductQty;
                if (p.StockQuantity < 0) p.StockQuantity = 0;
                p.UpdatedAt = DateTime.Now;

                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id,
                    ChangeType = "临期报损",
                    ChangeQty = -deductQty,
                    BeforeQty = beforeQty,
                    AfterQty = p.StockQuantity,
                    RefNo = $"批次{b.BatchNo}",
                    CreatedBy = _me.Id,
                    CreatedAt = DateTime.Now,
                });

                b.IsProcessed = true;
                b.ProcessedAt = DateTime.Now;

                totalQty += deductQty;
                if (!productNames.Contains(p.Name)) productNames.Add(p.Name);
            }

            await _db.SaveChangesAsync();

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id,
                UserName = _me.Username,
                IpAddress = _me.ClientIp,
                Module = "库存管理",
                Action = "临期报损",
                Target = $"处理 {batches.Count} 个批次，报损 {totalQty} 件（{string.Join("、", productNames.Take(3))}{(productNames.Count > 3 ? "等" : "")}）",
            });
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            return ApiResult<string>.Ok($"已处理 {batches.Count} 个批次，扣减库存 {totalQty} 件");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("临期处理失败：" + ex.Message, ex);
        }
    }
}
