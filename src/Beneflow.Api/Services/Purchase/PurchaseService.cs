using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>采购管理：进货单（移动加权平均成本）+ 采购退货</summary>
public class PurchaseService : IPurchaseService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;

    public PurchaseService(AppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    // ================= 进货单 =================

    public async Task<PagedResult<object>> ListAsync(string? keyword, string? dateFrom, string? dateTo, int page, int pageSize)
    {
        var q =
            from o in _db.PurchaseOrders.AsNoTracking()
            join s in _db.Suppliers on o.SupplierId equals s.Id
            join u in _db.Users on o.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword)
                  || o.OrderNo.Contains(keyword) || s.Name.Contains(keyword) || u.Name.Contains(keyword)
            select new { o, s.Name, UserName = u.Name };

        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            q = q.Where(x => x.o.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            q = q.Where(x => x.o.CreatedAt < dt.AddDays(1));

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);

        // 商品种类数
        var rows = await q.OrderByDescending(x => x.o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var orderIds = rows.Select(r => r.o.Id).ToList();
        var kindMap = await _db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => orderIds.Contains(d.OrderId))
            .GroupBy(d => d.OrderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var list = rows.Select(r => (object)new
        {
            id = r.o.Id,
            orderNo = r.o.OrderNo,
            supplierId = r.o.SupplierId,
            supplierName = r.Name,
            itemCount = kindMap.GetValueOrDefault(r.o.Id),
            totalQty = r.o.TotalQty,
            totalAmount = r.o.TotalAmount,
            remark = r.o.Remark,
            createdAt = r.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            createdByName = r.UserName,
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>进货单全量导出（按 keyword/dateFrom/dateTo 筛选，不分页）</summary>
    public async Task<List<Dictionary<string, object?>>> ExportListAsync(string? keyword, string? dateFrom, string? dateTo)
    {
        var q =
            from o in _db.PurchaseOrders.AsNoTracking()
            join s in _db.Suppliers on o.SupplierId equals s.Id
            join u in _db.Users on o.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword)
                  || o.OrderNo.Contains(keyword) || s.Name.Contains(keyword) || u.Name.Contains(keyword)
            select new { o, SupplierName = s.Name, UserName = u.Name };

        if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
            q = q.Where(x => x.o.CreatedAt >= df);
        if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
            q = q.Where(x => x.o.CreatedAt < dt.AddDays(1));

        var rows = await q.OrderByDescending(x => x.o.Id).ToListAsync();
        var orderIds = rows.Select(r => r.o.Id).ToList();
        var kindMap = await _db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => orderIds.Contains(d.OrderId))
            .GroupBy(d => d.OrderId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        return rows.Select(r => new Dictionary<string, object?>
        {
            ["orderNo"] = r.o.OrderNo,
            ["supplierName"] = r.SupplierName,
            ["itemCount"] = kindMap.GetValueOrDefault(r.o.Id),
            ["totalQty"] = r.o.TotalQty,
            ["totalAmount"] = r.o.TotalAmount,
            ["remark"] = r.o.Remark ?? "",
            ["createdAt"] = r.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            ["createdByName"] = r.UserName,
        }).ToList();
    }

    public async Task<ApiResult<object>> GetDetailAsync(int id)
    {
        var head = await (
            from o in _db.PurchaseOrders.AsNoTracking()
            join s in _db.Suppliers on o.SupplierId equals s.Id
            join u in _db.Users on o.CreatedBy equals u.Id
            where o.Id == id
            select new { o, s.Name, UserName = u.Name }).FirstOrDefaultAsync();
        if (head == null) return ApiResult<object>.Fail("进货单不存在");

        var details = await (
            from d in _db.PurchaseOrderDetails.AsNoTracking()
            join p in _db.Products on d.ProductId equals p.Id
            where d.OrderId == id
            select new { d, p.Barcode, ProductName = p.Name, p.Unit })
            .ToListAsync();

        return ApiResult<object>.Ok(new
        {
            orderNo = head.o.OrderNo,
            supplierName = head.Name,
            totalQty = head.o.TotalQty,
            totalAmount = head.o.TotalAmount,
            createdAt = head.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            createdByName = head.UserName,
            items = details.Select(d => new
            {
                productId = d.d.ProductId, barcode = d.Barcode, name = d.ProductName, unit = d.Unit,
                qty = d.d.Qty, costPrice = d.d.CostPrice, subTotal = d.d.SubTotal,
            }),
        });
    }

    /// <summary>
    /// 创建进货单：库存入账、按「移动加权平均法」更新成本价、写库存流水。
    /// </summary>
    public async Task<ApiResult<object>> CreateAsync(CreatePurchaseDto dto)
    {
        if (dto.Details == null || dto.Details.Count == 0)
            return ApiResult<object>.Fail("请添加商品明细");
        if (await _db.Suppliers.FindAsync(dto.SupplierId) == null)
            return ApiResult<object>.Fail("供应商不存在");
        if (dto.Details.Any(d => d.Qty <= 0)) return ApiResult<object>.Fail("进货数量必须大于 0");
        if (dto.Details.Any(d => d.CostPrice < 0)) return ApiResult<object>.Fail("进价不能为负");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var todayCount = await _db.PurchaseOrders.CountAsync(o => o.CreatedAt >= DateTime.Today);
            var orderNo = $"PO{DateTime.Now:yyyyMMdd}{todayCount + 1:D3}";

            var productIds = dto.Details.Select(d => d.ProductId).Distinct().ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            var order = new PurchaseOrder
            {
                OrderNo = orderNo, SupplierId = dto.SupplierId, Remark = dto.Remark,
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
                TotalQty = Math.Round(dto.Details.Sum(d => d.Qty), 3),
                TotalAmount = Math.Round(dto.Details.Sum(d => d.Qty * d.CostPrice), 2),
            };
            _db.PurchaseOrders.Add(order);
            await _db.SaveChangesAsync();

            foreach (var d in dto.Details.GroupBy(d => d.ProductId).Select(g =>
                     new PurchaseDetailDto
                     {
                         ProductId = g.Key, Qty = g.Sum(x => x.Qty),
                         CostPrice = g.First().CostPrice, ProduceDate = g.First().ProduceDate,
                     }))
            {
                if (!products.TryGetValue(d.ProductId, out var p))
                    return ApiResult<object>.Fail($"商品 ID {d.ProductId} 不存在");

                // 移动加权平均成本：(旧库存×旧成本 + 入库数量×进价) ÷ 新库存
                var newQty = p.StockQuantity + d.Qty;
                var oldCost = p.CostPrice;
                p.CostPrice = Math.Round((p.StockQuantity * p.CostPrice + d.Qty * d.CostPrice) / newQty, 2, MidpointRounding.AwayFromZero);
                p.StockQuantity = newQty;
                p.UpdatedAt = DateTime.Now;

                _db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
                {
                    OrderId = order.Id, ProductId = d.ProductId,
                    Qty = d.Qty, CostPrice = d.CostPrice,
                    SubTotal = Math.Round(d.Qty * d.CostPrice, 2),
                });

                // 有效期商品按批次入库
                if (p.HasExpiry && p.ShelfLifeDays > 0)
                {
                    var produce = d.ProduceDate ?? DateTime.Today;
                    _db.Batches.Add(new ProductBatch
                    {
                        ProductId = p.Id, BatchNo = $"B{produce:yyMMdd}{order.Id:D4}",
                        ProduceDate = produce,
                        ExpireDate = produce.AddDays(p.ShelfLifeDays),
                        Quantity = d.Qty,
                        CreatedAt = DateTime.Now,
                    });
                }

                _db.StockLogs.Add(new StockLog
                {
                    ProductId = d.ProductId, ChangeType = "采购入库", ChangeQty = d.Qty,
                    BeforeQty = newQty - d.Qty, AfterQty = newQty,
                    RefNo = orderNo, CreatedBy = _me.Id, CreatedAt = order.CreatedAt,
                });
            }

            await _db.SaveChangesAsync();
            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "采购管理",
                Action = "创建进货单", Target = $"{orderNo} 合计 ¥{order.TotalAmount}",
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult<object>.Ok(new { id = order.Id, orderNo });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("创建进货单失败：" + ex.Message, ex);
        }
    }

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
