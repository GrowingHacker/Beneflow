using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>库存管理：实时库存 / 库存预警 / 临期商品 / 盘点单 / 库存流水</summary>
public class StockService : IStockService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;

    public StockService(AppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    /// <summary>临期天数阈值（系统设置，默认 30）</summary>
    private async Task<int> ExpiryDaysAsync()
    {
        var v = await _db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == "stock").Select(c => c.ConfigValue).FirstOrDefaultAsync();
        if (v != null)
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(v);
                return json.RootElement.TryGetProperty("expiryDays", out var el) ? el.GetInt32() : 30;
            }
            catch { /* 忽略坏配置 */ }
        }
        return 30;
    }

    // ================= 实时库存（带状态标签） =================

    public async Task<object> InventoryListAsync(string? keyword, string? status, int page, int pageSize)
    {
        var expiryDays = await ExpiryDaysAsync();
        var q =
            from p in _db.Products.AsNoTracking()
            join c in _db.Categories on p.CategoryId equals c.Id
            where !p.IsDeleted &&
                  (string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) || p.Barcode.Contains(keyword))
            orderby p.Id
            select new
            {
                p.Id, p.Barcode, p.Name, CategoryName = c.Name,
                p.Unit, p.SalePrice, p.CostPrice,
                p.StockQuantity, p.SafetyStock, p.HasExpiry, p.ShelfLifeDays,
            };

        var all = await q.ToListAsync();

        // 最小剩余天数（未处理批次）
        var ids = all.Select(x => x.Id).ToList();
        var minLeft = await (
            from b in _db.Batches.AsNoTracking()
            where !b.IsProcessed && ids.Contains(b.ProductId)
            group b by b.ProductId into g
            select new { Pid = g.Key, Min = g.Min(b => b.ExpireDate) }).ToDictionaryAsync(x => x.Pid, x => x.Min);

        var rows = all.Select(p =>
        {
            int? left = null;
            string st;
            DateTime? expire = null;
            if (p.HasExpiry && minLeft.TryGetValue(p.Id, out var d))
            {
                left = (int)Math.Floor((d - DateTime.Today).TotalDays);
                expire = d;
            }
            if (p.StockQuantity <= 0) st = "缺货";
            else if (p.StockQuantity <= p.SafetyStock) st = "预警";
            else if (left.HasValue && left.Value < 0) st = "过期";
            else if (left.HasValue && left.Value <= expiryDays) st = "临期";
            else st = "正常";
            return new
            {
                id = p.Id, barcode = p.Barcode, name = p.Name, categoryName = p.CategoryName,
                unit = p.Unit, costPrice = p.CostPrice, salePrice = p.SalePrice,
                stockQuantity = p.StockQuantity, safetyStock = p.SafetyStock,
                stockAmount = Math.Round(p.StockQuantity * p.CostPrice, 2),
                hasExpiry = p.HasExpiry,
                expireDate = expire?.ToString("yyyy-MM-dd"),
                daysLeft = left,
                status = st,
            };
        });

        var filtered = string.IsNullOrEmpty(status)
            ? rows.Cast<object>()
            : rows.Where(r => r.status == status).Cast<object>();

        var total = filtered.Count();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var list = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new { list, total, page, pageSize };
    }

    /// <summary>导出实时库存全量（按 keyword/status 筛选，不分页），返回行字典</summary>
    public async Task<List<Dictionary<string, object?>>> ExportInventoryAsync(string? keyword, string? status)
    {
        var expiryDays = await ExpiryDaysAsync();
        var q =
            from p in _db.Products.AsNoTracking()
            join c in _db.Categories on p.CategoryId equals c.Id
            where !p.IsDeleted &&
                  (string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) || p.Barcode.Contains(keyword))
            orderby p.Id
            select new
            {
                p.Id, p.Barcode, p.Name, CategoryName = c.Name,
                p.Unit, p.SalePrice, p.CostPrice,
                p.StockQuantity, p.SafetyStock, p.HasExpiry,
            };

        var all = await q.ToListAsync();
        var ids = all.Select(x => x.Id).ToList();
        var minLeft = await (
            from b in _db.Batches.AsNoTracking()
            where !b.IsProcessed && ids.Contains(b.ProductId)
            group b by b.ProductId into g
            select new { Pid = g.Key, Min = g.Min(b => b.ExpireDate) }).ToDictionaryAsync(x => x.Pid, x => x.Min);

        var rows = all.Select(p =>
        {
            int? left = null; string st; DateTime? expire = null;
            if (p.HasExpiry && minLeft.TryGetValue(p.Id, out var d))
            { left = (int)Math.Floor((d - DateTime.Today).TotalDays); expire = d; }
            if (p.StockQuantity <= 0) st = "缺货";
            else if (p.StockQuantity <= p.SafetyStock) st = "预警";
            else if (left.HasValue && left.Value < 0) st = "过期";
            else if (left.HasValue && left.Value <= expiryDays) st = "临期";
            else st = "正常";
            return new Dictionary<string, object?>
            {
                ["barcode"] = p.Barcode, ["name"] = p.Name, ["categoryName"] = p.CategoryName,
                ["unit"] = p.Unit, ["costPrice"] = p.CostPrice, ["salePrice"] = p.SalePrice,
                ["stockQuantity"] = p.StockQuantity, ["safetyStock"] = p.SafetyStock,
                ["stockAmount"] = Math.Round(p.StockQuantity * p.CostPrice, 2),
                ["expireDate"] = expire?.ToString("yyyy-MM-dd"),
                ["status"] = st,
            };
        });
        var filtered = string.IsNullOrEmpty(status) ? rows : rows.Where(r => (string)r["status"]! == status);
        return filtered.ToList();
    }

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

    /// <summary>批量标记临期商品为已处理</summary>
    public async Task<ApiResult> MarkProcessedAsync(long[] ids)
    {
        if (ids == null || ids.Length == 0) return ApiResult.Fail("请选择要标记的记录");
        var batches = await _db.Batches.Where(b => ids.Contains(b.Id)).ToListAsync();
        foreach (var b in batches)
        {
            b.IsProcessed = true;
            b.ProcessedAt = DateTime.Now;
        }
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    // ================= 库存预警 =================

    public async Task<List<object>> WarningsAsync()
    {
        var rows = await (
            from p in _db.Products.AsNoTracking()
            join c in _db.Categories on p.CategoryId equals c.Id
            where !p.IsDeleted && p.StockQuantity <= p.SafetyStock
            orderby p.StockQuantity ascending
            select new { p, CategoryName = c.Name }).ToListAsync();

        return rows.Select(r => (object)new
        {
            id = r.p.Id, barcode = r.p.Barcode, name = r.p.Name,
            categoryName = r.CategoryName, unit = r.p.Unit,
            stockQuantity = r.p.StockQuantity, safetyStock = r.p.SafetyStock,
            shortage = Math.Max(0, r.p.SafetyStock - r.p.StockQuantity),   // 建议补货数量
            costPrice = r.p.CostPrice, salePrice = r.p.SalePrice,
            status = r.p.StockQuantity <= 0 ? "缺货" : "预警",
        }).ToList();
    }

    // ================= 库存流水 =================

    public async Task<PagedResult<object>> LogListAsync(string? keyword, string? changeType, int page, int pageSize)
    {
        var q =
            from l in _db.StockLogs.AsNoTracking()
            join p in _db.Products on l.ProductId equals p.Id
            join u in _db.Users on l.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) ||
                  p.Barcode.Contains(keyword) || l.RefNo.Contains(keyword)
            select new { l, p.Name, p.Barcode, UserName = u.Name };

        if (!string.IsNullOrEmpty(changeType)) q = q.Where(x => x.l.ChangeType == changeType);

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.OrderByDescending(x => x.l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(r => (object)new
        {
            id = r.l.Id, productName = r.Name, barcode = r.Barcode,
            changeType = r.l.ChangeType, changeQty = r.l.ChangeQty,
            beforeQty = r.l.BeforeQty, afterQty = r.l.AfterQty,
            refNo = r.l.RefNo, createdAt = r.l.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            createdByName = r.UserName,
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>导出库存流水全量（按 keyword/changeType 筛选，不分页），返回行字典</summary>
    public async Task<List<Dictionary<string, object?>>> ExportLogAsync(string? keyword, string? changeType)
    {
        var q =
            from l in _db.StockLogs.AsNoTracking()
            join p in _db.Products on l.ProductId equals p.Id
            join u in _db.Users on l.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword) || p.Name.Contains(keyword) ||
                  p.Barcode.Contains(keyword) || l.RefNo.Contains(keyword)
            select new { l, p.Name, p.Barcode, UserName = u.Name };
        if (!string.IsNullOrEmpty(changeType)) q = q.Where(x => x.l.ChangeType == changeType);
        var rows = await q.OrderByDescending(x => x.l.Id).ToListAsync();
        return rows.Select(r => new Dictionary<string, object?>
        {
            ["createdAt"] = r.l.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            ["productName"] = r.Name, ["barcode"] = r.Barcode,
            ["changeType"] = r.l.ChangeType, ["changeQty"] = r.l.ChangeQty,
            ["beforeQty"] = r.l.BeforeQty, ["afterQty"] = r.l.AfterQty,
            ["refNo"] = r.l.RefNo, ["createdByName"] = r.UserName,
        }).ToList();
    }
}
