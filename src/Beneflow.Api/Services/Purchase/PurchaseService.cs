using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Beneflow.Api.Services;

/// <summary>采购管理：进货单（移动加权平均成本）+ 采购退货</summary>
public partial class PurchaseService : IPurchaseService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly StockMutationLock _stockLock;

    public PurchaseService(AppDbContext db, ICurrentUser me, StockMutationLock stockLock)
    { _db = db; _me = me; _stockLock = stockLock; }

    // ================= 进货单 =================

    public async Task<PagedResult<object>> ListAsync(string? keyword, string? dateFrom, string? dateTo, int page, int pageSize, int? supplierId = null)
    {
        var q =
            from o in _db.PurchaseOrders.AsNoTracking()
            join s in _db.Suppliers on o.SupplierId equals s.Id
            join u in _db.Users on o.CreatedBy equals u.Id
            where string.IsNullOrEmpty(keyword)
                  || o.OrderNo.Contains(keyword) || s.Name.Contains(keyword) || u.Name.Contains(keyword)
            select new { o, s.Name, UserName = u.Name };

        // 供应商精确筛选（供应商列表的「进货记录」弹窗）：与 keyword 是「与」的关系
        if (supplierId.HasValue)
            q = q.Where(x => x.o.SupplierId == supplierId.Value);

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
            isVoided = r.o.IsVoided,
            voidedAt = r.o.VoidedAt.HasValue ? r.o.VoidedAt.Value.ToString("yyyy-MM-dd HH:mm") : null,
            createdAt = r.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            createdByName = r.UserName,
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
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
            select new { d, p.Barcode, ProductName = p.Name, p.Unit, p.IsWeighted })
            .ToListAsync();

        return ApiResult<object>.Ok(new
        {
            id = head.o.Id,
            orderNo = head.o.OrderNo,
            supplierId = head.o.SupplierId,
            supplierName = head.Name,
            totalQty = head.o.TotalQty,
            totalAmount = head.o.TotalAmount,
            isVoided = head.o.IsVoided,
            voidedAt = head.o.VoidedAt.HasValue ? head.o.VoidedAt.Value.ToString("yyyy-MM-dd HH:mm") : null,
            createdAt = head.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
            createdByName = head.UserName,
            remark = head.o.Remark,
            items = details.Select(d => new
            {
                productId = d.d.ProductId, barcode = d.Barcode, name = d.ProductName, unit = d.Unit,
                isWeighted = d.IsWeighted,
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

        // 加锁顺序全局统一：先单据号锁，再商品库存锁（顺序反了会和并发建单构成死锁环）
        using (await _stockLock.AcquireOrderNoAsync())
        using (await _stockLock.AcquireAsync(dto.Details.Select(d => d.ProductId)))
        {
            return await CreateCoreAsync(dto);
        }
    }

    /// <summary>创建进货单的实际逻辑；调用方须已持有单据号锁与相关商品的库存锁。
    /// 传 sharedTx 时复用外层事务（本方法不提交、不回滚，交由调用方整包收尾），供批量导入使用；
    /// 不传则自开事务、成功提交、失败回滚。</summary>
    private async Task<ApiResult<object>> CreateCoreAsync(CreatePurchaseDto dto, IDbContextTransaction? sharedTx = null)
    {
        var ownsTx = sharedTx is null;
        var tx = sharedTx ?? await _db.Database.BeginTransactionAsync();
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
            if (ownsTx) await tx.CommitAsync();
            return ApiResult<object>.Ok(new { id = order.Id, orderNo });
        }
        catch (Exception ex)
        {
            if (ownsTx) await tx.RollbackAsync();
            throw new InvalidOperationException("创建进货单失败：" + ex.Message, ex);
        }
        finally
        {
            // 共享事务的生命周期归调用方（批量导入整包提交），这里不能替它释放
            if (ownsTx) await tx.DisposeAsync();
        }
    }

    /// <summary>
    /// 作废进货单：回退库存、删除对应批次、标记作废。
    /// 已发生采购退货的订单不允许作废（避免库存逻辑复杂化）。
    /// 成本价回退：将该次入库的移动加权平均影响回滚。
    /// </summary>
    public async Task<ApiResult> VoidAsync(int id)
    {
        // 只读预取涉及的商品用于加锁（此步不修改数据）。
        // 「是否已作废」判定留在锁内，因此同一单的并发作废会被串行化，不会重复回退库存。
        var productIds = await _db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.OrderId == id).Select(d => d.ProductId).Distinct().ToListAsync();

        using (await _stockLock.AcquireAsync(productIds))
        {
            return await VoidCoreAsync(id);
        }
    }

    /// <summary>作废进货单的实际逻辑；调用方须已持有相关商品的库存锁。</summary>
    private async Task<ApiResult> VoidCoreAsync(int id)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var order = await _db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return ApiResult.Fail("进货单不存在");
            if (order.IsVoided) return ApiResult.Fail("该进货单已作废，无需重复操作");

            var details = await _db.PurchaseOrderDetails
                .Where(d => d.OrderId == id).Include(d => d.Product).ToListAsync();
            if (details.Count == 0) return ApiResult.Fail("进货单缺少明细，无法作废");

            // 已发生采购退货的订单不允许作废
            var hasReturn = await _db.PurchaseReturns.AnyAsync(r => r.SupplierId == order.SupplierId
                && _db.PurchaseReturnDetails.Any(rd => rd.ReturnId == r.Id
                    && details.Select(d => d.ProductId).Contains(rd.ProductId)));
            if (hasReturn)
                return ApiResult.Fail("该进货单关联商品已发生采购退货，不能作废，请通过退货流程处理");

            // 回退库存和成本（移动加权平均逆运算）
            // 回退后成本 = (当前库存×当前成本 - 原入库数量×原进价) / (当前库存 - 原入库数量)
            foreach (var d in details)
            {
                var p = d.Product;
                if (p.StockQuantity < d.Qty)
                    return ApiResult.Fail($"商品「{p.Name}」当前库存 {p.StockQuantity}，不足作废回退数量 {d.Qty}");

                var currentTotal = p.StockQuantity * p.CostPrice;
                var oldTotal = d.Qty * d.CostPrice;
                var newQty = p.StockQuantity - d.Qty;
                if (newQty > 0)
                {
                    p.CostPrice = Math.Round((currentTotal - oldTotal) / newQty, 2, MidpointRounding.AwayFromZero);
                }
                else
                {
                    p.CostPrice = 0; // 库存归零，成本清零
                }

                var before = p.StockQuantity;
                p.StockQuantity = newQty;
                p.UpdatedAt = DateTime.Now;

                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id,
                    ChangeType = "作废回退",
                    ChangeQty = -d.Qty,
                    BeforeQty = before,
                    AfterQty = p.StockQuantity,
                    RefNo = order.OrderNo,
                    CreatedBy = _me.Id,
                    CreatedAt = DateTime.Now,
                });
            }

            // 删除对应批次（有效期商品）
            var batches = await _db.Batches
                .Where(b => details.Select(d => d.ProductId).Contains(b.ProductId)
                    && b.BatchNo.EndsWith(order.Id.ToString("D4")))
                .ToListAsync();
            _db.Batches.RemoveRange(batches);

            order.IsVoided = true;
            order.VoidedAt = DateTime.Now;

            await _db.SaveChangesAsync();

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "采购管理",
                Action = "作废进货单",
                Target = $"{order.OrderNo} 合计 ¥{order.TotalAmount}，回退 {details.Sum(d => d.Qty)} 件",
            });
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            return ApiResult.Ok();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("作废进货单失败：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 编辑进货单：直接修改原单，单号和创建时间不变。
    /// 逻辑：先回退原单对库存/成本/批次的全部影响，再按新数据重新入库。
    /// 可修改：供应商、商品、数量、进价、备注。
    /// </summary>
    public async Task<ApiResult<object>> UpdateAsync(int id, CreatePurchaseDto dto)
    {
        if (dto.Details == null || dto.Details.Count == 0)
            return ApiResult<object>.Fail("请添加商品明细");
        if (await _db.Suppliers.FindAsync(dto.SupplierId) == null)
            return ApiResult<object>.Fail("供应商不存在");
        if (dto.Details.Any(d => d.Qty <= 0)) return ApiResult<object>.Fail("进货数量必须大于 0");
        if (dto.Details.Any(d => d.CostPrice < 0)) return ApiResult<object>.Fail("进价不能为负");

        // 编辑会先回退旧明细、再按新明细入库，新旧两批商品都要加锁。
        // 单号沿用原单（不新建单据），因此无需单据号锁。
        var oldProductIds = await _db.PurchaseOrderDetails.AsNoTracking()
            .Where(d => d.OrderId == id).Select(d => d.ProductId).ToListAsync();
        var lockIds = oldProductIds.Concat(dto.Details.Select(d => d.ProductId)).Distinct().ToList();

        using (await _stockLock.AcquireAsync(lockIds))
        {
            return await UpdateCoreAsync(id, dto);
        }
    }

    /// <summary>编辑进货单的实际逻辑；调用方须已持有新旧两批商品的库存锁。</summary>
    private async Task<ApiResult<object>> UpdateCoreAsync(int id, CreatePurchaseDto dto)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var order = await _db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return ApiResult<object>.Fail("进货单不存在");
            if (order.IsVoided) return ApiResult<object>.Fail("已作废的进货单不能修改");

            var oldDetails = await _db.PurchaseOrderDetails
                .Where(d => d.OrderId == id).Include(d => d.Product).ToListAsync();

            // 已发生采购退货的商品不允许修改（避免库存逻辑混乱）
            var oldProductIds = oldDetails.Select(d => d.ProductId).ToList();
            var hasReturn = await _db.PurchaseReturnDetails
                .AnyAsync(rd => oldProductIds.Contains(rd.ProductId));
            if (hasReturn)
                return ApiResult<object>.Fail("该进货单关联商品已发生采购退货，不能修改，请通过退货+新增方式处理");

            // ========== 第一步：回退原单的库存影响 ==========
            foreach (var d in oldDetails)
            {
                var p = d.Product;
                if (p.StockQuantity < d.Qty)
                    return ApiResult<object>.Fail($"商品「{p.Name}」当前库存 {p.StockQuantity}，不足回退数量 {d.Qty}（可能已发生销售）");

                // 回退成本：移动加权平均的逆运算
                // 回退后成本 = (当前库存×当前成本 - 原入库数量×原进价) / (当前库存 - 原入库数量)
                var currentTotal = p.StockQuantity * p.CostPrice;
                var oldTotal = d.Qty * d.CostPrice;
                var newQty = p.StockQuantity - d.Qty;
                if (newQty > 0)
                {
                    p.CostPrice = Math.Round((currentTotal - oldTotal) / newQty, 2, MidpointRounding.AwayFromZero);
                }
                else
                {
                    p.CostPrice = 0; // 库存归零，成本清零
                }

                var before = p.StockQuantity;
                p.StockQuantity = newQty;
                p.UpdatedAt = DateTime.Now;

                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id,
                    ChangeType = "进货单修改-回退",
                    ChangeQty = -d.Qty,
                    BeforeQty = before,
                    AfterQty = p.StockQuantity,
                    RefNo = order.OrderNo,
                    CreatedBy = _me.Id,
                    CreatedAt = DateTime.Now,
                });
            }

            // 删除原批次
            var oldBatches = await _db.Batches
                .Where(b => oldProductIds.Contains(b.ProductId)
                    && b.BatchNo.EndsWith(order.Id.ToString("D4")))
                .ToListAsync();
            _db.Batches.RemoveRange(oldBatches);

            // 删除原明细
            _db.PurchaseOrderDetails.RemoveRange(oldDetails);

            // ========== 第二步：按新数据重新入库 ==========
            var newProductIds = dto.Details.Select(d => d.ProductId).Distinct().ToList();
            var products = await _db.Products.Where(p => newProductIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            var detailGroups = dto.Details.GroupBy(d => d.ProductId).Select(g =>
                new PurchaseDetailDto
                {
                    ProductId = g.Key, Qty = g.Sum(x => x.Qty),
                    CostPrice = g.First().CostPrice, ProduceDate = g.First().ProduceDate,
                }).ToList();

            foreach (var d in detailGroups)
            {
                if (!products.TryGetValue(d.ProductId, out var p))
                    return ApiResult<object>.Fail($"商品 ID {d.ProductId} 不存在");

                // 移动加权平均成本
                var oldQty = p.StockQuantity;
                var newQty = oldQty + d.Qty;
                if (newQty > 0)
                {
                    p.CostPrice = Math.Round((oldQty * p.CostPrice + d.Qty * d.CostPrice) / newQty, 2, MidpointRounding.AwayFromZero);
                }
                else
                {
                    p.CostPrice = d.CostPrice;
                }
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
                    ProductId = d.ProductId, ChangeType = "进货单修改-入库", ChangeQty = d.Qty,
                    BeforeQty = oldQty, AfterQty = newQty,
                    RefNo = order.OrderNo, CreatedBy = _me.Id, CreatedAt = DateTime.Now,
                });
            }

            // ========== 第三步：更新主表 ==========
            order.SupplierId = dto.SupplierId;
            order.Remark = dto.Remark;
            order.TotalQty = Math.Round(detailGroups.Sum(d => d.Qty), 3);
            order.TotalAmount = Math.Round(detailGroups.Sum(d => d.Qty * d.CostPrice), 2);

            await _db.SaveChangesAsync();

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "采购管理",
                Action = "修改进货单",
                Target = $"{order.OrderNo} 合计 ¥{order.TotalAmount}，共 {detailGroups.Count} 种商品",
            });
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            return ApiResult<object>.Ok(new { id = order.Id, orderNo = order.OrderNo });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("修改进货单失败：" + ex.Message, ex);
        }
    }
}
