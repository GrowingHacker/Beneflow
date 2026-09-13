using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>销售管理：收银结算 + 赊账 + 销售退货</summary>
public partial class SaleService : ISaleService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly StockMutationLock _stockLock;

    public SaleService(AppDbContext db, ICurrentUser me, StockMutationLock stockLock)
    { _db = db; _me = me; _stockLock = stockLock; }

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

        // 赊账订单的欠款结清状态（按当前页订单聚合）
        var creditAgg = await _db.CreditSales.AsNoTracking()
            .Where(c => ids.Contains(c.SaleOrderId))
            .Select(c => new { c.SaleOrderId, c.Status })
            .ToDictionaryAsync(x => x.SaleOrderId, x => x.Status);

        var list = rows.Select(r =>
        {
            retAgg.TryGetValue(r.o.Id, out var agg);
            bool? settled = null;
            if (r.o.IsCredit && creditAgg.TryGetValue(r.o.Id, out var st)) settled = st;
            return (object)new
            {
                id = r.o.Id, orderNo = r.o.OrderNo,
                totalAmount = r.o.TotalAmount, discountAmount = r.o.DiscountAmount,
                payAmount = r.o.PayAmount, payMethod = r.o.PayMethod,
                cashAmount = r.o.CashAmount, changeAmount = r.o.ChangeAmount,
                status = StatusText(r.o.IsVoided, agg?.Qty ?? 0, agg?.Ret ?? 0),
                isCredit = r.o.IsCredit,
                creditSettled = settled,
                wechatId = r.o.WechatId,
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
        bool? settled = null;
        if (o.IsCredit)
        {
            var cs = await _db.CreditSales.AsNoTracking().Where(c => c.SaleOrderId == o.Id).Select(c => (bool?)c.Status).FirstOrDefaultAsync();
            if (cs.HasValue) settled = cs;
        }
        return new
        {
            id = o.Id, orderNo = o.OrderNo,
            totalAmount = o.TotalAmount, discountAmount = o.DiscountAmount,
            payAmount = o.PayAmount, payMethod = o.PayMethod,
            cashAmount = o.CashAmount, changeAmount = o.ChangeAmount,
            status = StatusText(o.IsVoided, details.Sum(d => d.qty), details.Sum(d => d.returnedQty)),
            isCredit = o.IsCredit, creditSettled = settled, wechatId = o.WechatId,
            createdAt = o.CreatedAt.ToString("yyyy-MM-dd HH:mm"), createdByName = userName ?? "",
            items = details,
        };
    }

    /// <summary>由 IsVoided 优先判断作废，否则按明细退货数量推导：未退=已完成 / 全退=已退货 / 部分退=部分退货</summary>
    private static string StatusText(bool isVoided, decimal qty, decimal returned) =>
        isVoided ? "已作废" : returned <= 0 ? "已完成" : returned >= qty ? "已退货" : "部分退货";

    /// <summary>
    /// 收银结算：校验库存 → 扣库存（快照成本价）→ 写流水；赊账同时生成 CreditSale 欠款记录。
    /// 整个过程持有「单据号锁 + 相关商品库存锁」，防止并发收银丢更新 / 单号撞车。
    /// </summary>
    public async Task<ApiResult<object>> CreateAsync(CreateSaleDto dto)
    {
        if (dto.Items == null || dto.Items.Count == 0)
            return ApiResult<object>.Fail("购物清单为空");

        // 加锁顺序全局统一：先单据号锁，再商品库存锁（顺序反了会和建单请求构成死锁环）。
        using (await _stockLock.AcquireOrderNoAsync())
        using (await _stockLock.AcquireAsync(dto.Items.Select(i => i.ProductId)))
        {
            return await CreateCoreAsync(dto);
        }
    }

    /// <summary>创建销售单的实际逻辑；调用方须已持有单据号锁与相关商品的库存锁。</summary>
    private async Task<ApiResult<object>> CreateCoreAsync(CreateSaleDto dto)
    {
        if (dto.PayMethod == "赊账" && !dto.IsCredit)
            dto.IsCredit = true;

        if (dto.Items.Any(i => i.Qty <= 0)) return ApiResult<object>.Fail("商品数量必须大于 0");

        // 校验：非称重商品数量必须为整数
        var weightedIds = (await _db.Products.AsNoTracking()
            .Where(p => p.IsWeighted).Select(p => p.Id).ToListAsync()).ToHashSet();
        foreach (var item in dto.Items)
        {
            if (!weightedIds.Contains(item.ProductId) && item.Qty != Math.Truncate(item.Qty))
                return ApiResult<object>.Fail($"商品数量必须为整数（称重商品才允许小数）");
        }

        // 系统设置是否允许赊账
        var saleConfigJson = await _db.SystemConfigs.AsNoTracking()
            .Where(c => c.ConfigKey == "sale").Select(c => c.ConfigValue).FirstOrDefaultAsync();
        var allowCredit = true; // 默认允许
        if (!string.IsNullOrEmpty(saleConfigJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(saleConfigJson);
                if (doc.RootElement.TryGetProperty("allowCredit", out var prop)
                    && prop.ValueKind == System.Text.Json.JsonValueKind.False)
                {
                    allowCredit = false;
                }
            }
            catch { /* JSON 解析失败时默认允许赊账，不影响正常使用 */ }
        }
        if (dto.IsCredit && !allowCredit)
            return ApiResult<object>.Fail("系统设置不允许赊账，请更换收款方式");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var todayCount = await _db.SaleOrders.CountAsync(o => o.CreatedAt >= DateTime.Today);
            var orderNo = $"SO{DateTime.Now:yyyyMMdd}{todayCount + 1:D3}";

            var productIds = dto.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            // 按商品聚合（同一商品多行合并数量），同时校验库存
            var groupedItems = dto.Items.GroupBy(i => i.ProductId).Select(g =>
                new SaleItemDto { ProductId = g.Key, Qty = g.Sum(x => x.Qty) }).ToList();

            foreach (var item in groupedItems)
            {
                if (!products.TryGetValue(item.ProductId, out var p))
                    return ApiResult<object>.Fail($"商品 ID {item.ProductId} 不存在");
                if (p.StockQuantity < item.Qty)
                    return ApiResult<object>.Fail($"商品「{p.Name}」库存不足（当前 {p.StockQuantity}）");
            }

            // ===== 后端重算所有金额（前端金额仅作展示参考，后端以数据库售价为准） =====
            // 1. 计算商品明细：单价取数据库售价（确保不会被前端篡改价格）
            var detailList = new List<(Product p, decimal qty, decimal unitPrice, decimal subTotal)>();
            var calcTotalAmount = 0m;
            foreach (var item in groupedItems)
            {
                var p = products[item.ProductId];
                var unitPrice = p.SalePrice;   // 以数据库售价为准
                var subTotal = Math.Round(item.Qty * unitPrice, 2);
                detailList.Add((p, item.Qty, unitPrice, subTotal));
                calcTotalAmount += subTotal;
            }
            calcTotalAmount = Math.Round(calcTotalAmount, 2);

            // 2. 优惠金额：取前端传入值，但限制在合理范围（0 ~ 总金额）
            var discountAmount = Math.Round(dto.DiscountAmount, 2);
            if (discountAmount < 0) discountAmount = 0;
            if (discountAmount > calcTotalAmount) discountAmount = calcTotalAmount;

            // 3. 实付金额 = 总金额 - 优惠金额
            var payAmount = Math.Round(calcTotalAmount - discountAmount, 2);
            if (payAmount < 0) payAmount = 0;

            // 4. 现金实收和找零（仅现金支付方式有效）
            var isCash = dto.PayMethod == "现金";
            var cashAmount = isCash ? Math.Round(dto.CashAmount, 2) : payAmount;
            if (isCash && cashAmount < payAmount)
                return ApiResult<object>.Fail($"实收金额不足（应收 ¥{payAmount}）");
            var changeAmount = isCash ? Math.Round(cashAmount - payAmount, 2) : 0;
            if (changeAmount < 0) changeAmount = 0;

            var so = new SaleOrder
            {
                OrderNo = orderNo,
                TotalAmount = calcTotalAmount,
                DiscountAmount = discountAmount,
                PayAmount = payAmount,
                PayMethod = dto.PayMethod,
                CashAmount = cashAmount,
                ChangeAmount = changeAmount,
                IsCredit = dto.IsCredit,
                WechatId = dto.IsCredit ? (string.IsNullOrWhiteSpace(dto.WechatId) ? null : dto.WechatId!.Trim()) : null,
                Remark = dto.Remark,
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            };
            _db.SaleOrders.Add(so);
            await _db.SaveChangesAsync();

            foreach (var (p, qty, unitPrice, subTotal) in detailList)
            {
                var before = p.StockQuantity;
                p.StockQuantity -= qty;
                p.UpdatedAt = DateTime.Now;

                _db.SaleOrderDetails.Add(new SaleOrderDetail
                {
                    OrderId = so.Id, ProductId = p.Id,
                    ProductName = p.Name,
                    Barcode = p.Barcode,
                    Quantity = qty,
                    UnitPrice = unitPrice,
                    CostPrice = p.CostPrice,   // 销售时成本快照，毛利核算依据
                    SubTotal = subTotal,
                });
                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id, ChangeType = "销售出库", ChangeQty = -qty,
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

    /// <summary>作废销售单：回补库存、写流水、同步作废赊账记录；已发生退货的订单不允许作废。</summary>
    public async Task<ApiResult> VoidAsync(int id)
    {
        // 先只读地取出涉及的商品用于加锁（此步不修改任何数据）。
        // 「是否已作废」的判定保留在锁内，因此同一订单的并发作废会被串行化，不会重复回补库存。
        var productIds = await _db.SaleOrderDetails.AsNoTracking()
            .Where(d => d.OrderId == id).Select(d => d.ProductId).Distinct().ToListAsync();

        using (await _stockLock.AcquireAsync(productIds))
        {
            return await VoidCoreAsync(id);
        }
    }

    /// <summary>作废销售单的实际逻辑；调用方须已持有相关商品的库存锁。</summary>
    private async Task<ApiResult> VoidCoreAsync(int id)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var o = await _db.SaleOrders.FirstOrDefaultAsync(x => x.Id == id);
            if (o == null) return ApiResult.Fail("销售单不存在");
            if (o.IsVoided) return ApiResult.Fail("该订单已作废，无需重复操作");

            var details = await _db.SaleOrderDetails.Where(d => d.OrderId == o.Id).ToListAsync();
            if (details.Count == 0) return ApiResult.Fail("销售单缺少明细，无法作废");

            // 已发生退货的订单不允许作废：退货已回补了部分库存，作废时整单回补会导致库存虚增
            if (details.Any(d => d.ReturnedQuantity > 0))
                return ApiResult.Fail("该订单已发生退货，不能作废，请通过退货流程处理");

            // 回补库存
            var productIds = details.Select(d => d.ProductId).Distinct().ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            foreach (var d in details)
            {
                if (!products.TryGetValue(d.ProductId, out var p))
                    return ApiResult.Fail($"商品「{d.ProductName}」已被删除，无法回补库存");

                var before = p.StockQuantity;
                p.StockQuantity += d.Quantity;
                p.UpdatedAt = DateTime.Now;

                _db.StockLogs.Add(new StockLog
                {
                    ProductId = p.Id,
                    ChangeType = "作废回补",
                    ChangeQty = d.Quantity,
                    BeforeQty = before,
                    AfterQty = p.StockQuantity,
                    RefNo = o.OrderNo,
                    CreatedBy = _me.Id,
                    CreatedAt = DateTime.Now,
                });
            }

            // 赊账订单作废：同步作废赊账记录（仅未结清的；已结清的因为钱已经收了，作废需人工线下处理）
            if (o.IsCredit)
            {
                var credit = await _db.CreditSales.FirstOrDefaultAsync(c => c.SaleOrderId == o.Id);
                if (credit != null && !credit.Status)
                {
                    // 未结清的赊账：直接标记结清并备注作废，避免欠款统计虚高
                    credit.Status = true;
                    credit.SettledAt = DateTime.Now;
                    credit.Remark = string.IsNullOrEmpty(credit.Remark)
                        ? "订单作废，赊账自动清零"
                        : credit.Remark + "（订单作废，赊账清零）";
                }
            }

            o.IsVoided = true;

            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp,
                Module = "销售管理",
                Action = "作废订单",
                Target = $"{o.OrderNo} 实收 ¥{o.PayAmount} ({o.PayMethod})，回补库存 {details.Sum(d => d.Quantity)} 件",
            });

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return ApiResult.Ok();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("作废销售单失败：" + ex.Message, ex);
        }
    }
}
