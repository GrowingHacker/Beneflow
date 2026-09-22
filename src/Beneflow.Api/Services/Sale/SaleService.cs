using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
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

        // 合计口径（行业惯例）：与列表同过滤条件，但剔除已作废单据——
        // 作废单在单据列表里仍可查到，却不参与任何金额统计；否则合计会虚高。
        // 实收合计口径：只算真正到手的钱。赊账单的实收 = 累计已还款额（还款时回写到原单），
        // 未还部分不存在单里，自然不计入；作废单整单剔除。
        // 注意这是「订单口径」——还款当天更新的是原单的实收，因此该笔钱记在原单日期，不记还款日。
        // GroupBy(1) 聚合最多返回一行：ToList 让聚合留在 SQL 端，又避免 First 生成 TOP(1)
        // （join + GroupBy 会把 WHERE 下压进子查询，外层 SelectExpression 既无谓词也无排序，EF 会告警）
        var sum = (await q.Where(x => !x.o.IsVoided)
            .GroupBy(x => 1)
            .Select(g => new
            {
                Orders = g.Count(),
                TotalAmount = g.Sum(x => x.o.TotalAmount),
                DiscountAmount = g.Sum(x => x.o.DiscountAmount),
                RoundOffAmount = g.Sum(x => x.o.RoundOffAmount),
                PayAmount = g.Sum(x => x.o.PayAmount),
                ReceivedAmount = g.Sum(x => x.o.ReceivedAmount),
                ChangeAmount = g.Sum(x => x.o.ChangeAmount),
            }).ToListAsync()).FirstOrDefault();

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
                totalAmount = r.o.TotalAmount,          // 商品总额（成交合计）
                discountAmount = r.o.DiscountAmount,    // 整单优惠金额
                discountRate = r.o.DiscountRate,        // 折扣率（折），按金额录入时为 null
                roundOffAmount = r.o.RoundOffAmount,    // 抹零金额
                payAmount = r.o.PayAmount,              // 应收金额
                receivedAmount = r.o.ReceivedAmount,    // 实收金额
                payMethod = r.o.PayMethod,
                cashAmount = r.o.CashAmount,            // 收款额（仅现金单）
                changeAmount = r.o.ChangeAmount,        // 找零（仅现金单）
                status = StatusText(r.o.IsVoided, agg?.Qty ?? 0, agg?.Ret ?? 0),
                isCredit = r.o.IsCredit,
                creditSettled = settled,
                wechatId = r.o.WechatId,
                createdAt = r.o.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                createdByName = r.UserName,
            };
        }).ToList();
        return new PagedResult<object>
        {
            List = list,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Summary = new
            {
                orders = sum?.Orders ?? 0,
                totalAmount = Math.Round(sum?.TotalAmount ?? 0, 2),
                discountAmount = Math.Round(sum?.DiscountAmount ?? 0, 2),
                roundOffAmount = Math.Round(sum?.RoundOffAmount ?? 0, 2),
                payAmount = Math.Round(sum?.PayAmount ?? 0, 2),
                receivedAmount = Math.Round(sum?.ReceivedAmount ?? 0, 2),
                changeAmount = Math.Round(sum?.ChangeAmount ?? 0, 2),
                // 折扣率不进合计：折率是「率」不可加总（8 折 + 9 折没有意义），报表看优惠金额合计
            },
        };
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
                originalPrice = d.OriginalPrice,   // 挂牌价快照：让利 = (挂牌价 − 成交价) × 数量，按行可见
                returnedQty = d.ReturnedQuantity, subTotal = d.SubTotal,
            }).ToListAsync();
        bool? settled = null;
        if (o.IsCredit)
        {
            var cs = await _db.CreditSales.AsNoTracking().Where(c => c.SaleOrderId == o.Id).Select(c => (bool?)c.Status).FirstOrDefaultAsync();
            if (cs.HasValue) settled = cs;
        }
        // 混合支付的收款构成（单项支付没有明细行，前端按 PayMethod 显示即可）
        var payments = await _db.SaleOrderPayments.AsNoTracking()
            .Where(p => p.OrderId == o.Id)
            .OrderBy(p => p.Id)
            .Select(p => new { payMethod = p.PayMethod, amount = p.Amount })
            .ToListAsync();
        return new
        {
            id = o.Id, orderNo = o.OrderNo,
            totalAmount = o.TotalAmount, discountAmount = o.DiscountAmount,
            discountRate = o.DiscountRate,
            roundOffAmount = o.RoundOffAmount,
            payAmount = o.PayAmount, receivedAmount = o.ReceivedAmount,
            payMethod = o.PayMethod,
            cashAmount = o.CashAmount, changeAmount = o.ChangeAmount,
            status = StatusText(o.IsVoided, details.Sum(d => d.qty), details.Sum(d => d.returnedQty)),
            isCredit = o.IsCredit, creditSettled = settled, wechatId = o.WechatId,
            createdAt = o.CreatedAt.ToString("yyyy-MM-dd HH:mm"), createdByName = userName ?? "",
            items = details,
            payments = payments,
        };
    }

    /// <summary>由 IsVoided 优先判断作废，否则按明细退货数量推导：未退=已完成 / 全退=已退货 / 部分退=部分退货</summary>
    private static string StatusText(bool isVoided, decimal qty, decimal returned) =>
        isVoided ? "已作废" : returned <= 0 ? "已完成" : returned >= qty ? "已退货" : "部分退货";

    /// <summary>
    /// 收银结算：校验库存 → 扣库存（快照成本价）→ 写流水；赊账同时生成 CreditSale 欠款记录。
    /// 支持单项支付（<see cref="CreateSaleDto.Payments"/> 为空）与混合支付（非空）两种形态，
    /// 后者一笔单可拆多种收款方式、实收不足的部分自动挂账，明细落 <see cref="SaleOrderPayment"/>。
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

        // 系统设置：是否允许赊账 + 让利限额（同一条 sale 配置，一次读出来）
        var saleCfg = await ReadSaleConfigAsync();
        if (dto.IsCredit && !saleCfg.AllowCredit)
            return ApiResult<object>.Fail("系统设置不允许赊账，请更换收款方式");

        // 让利风控：限额 → 授权 → 留痕。**放在事务之前** —— 拒绝时写的日志才不会被事务回滚掉，
        // 而且不必先开事务、查一遍商品、再白回滚一次（判定只看 dto，不需要商品数据）。
        var fence = await CheckDiscountFenceAsync(dto, saleCfg.Limits);
        if (!fence.Pass) return ApiResult<object>.Fail(fence.Message);

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
                var p = products[item.ProductId];
                if (p == null)
                    return ApiResult<object>.Fail($"商品 ID {item.ProductId} 不存在");
                if (p.StockQuantity < item.Qty)
                    return ApiResult<object>.Fail($"商品「{p.Name}」库存不足（当前 {p.StockQuantity}）");
            }

            // ===== 后端重算所有金额（前端金额仅作展示参考） =====
            // 1. 计算商品明细：成交单价 = 挂牌价经「档案优惠」折算后的价（前端传的 unitPrice 一律不采信）
            //    优惠方案只在商品档案里定义，收银台只负责执行，因此定价的唯一依据就是数据库 + 档案
            var now = DateTime.Now;
            var detailList = new List<(Product p, decimal qty, decimal unitPrice, decimal originalPrice, decimal subTotal)>();
            // 两个合计口径（小票惯例）：商品总额按「未优惠前」的挂牌价算，成交合计才是真正要收的钱，
            // 两者之差＝档案促销让利，落到单据的「优惠」上，让利因此在单据上可见、可统计。
            var listAmount = 0m;   // 原价合计 = Σ(挂牌价 × 数量)
            var dealAmount = 0m;   // 成交合计 = Σ(成交价 × 数量)
            foreach (var item in groupedItems)
            {
                var p = products[item.ProductId];
                var unitPrice = PromoHelper.EffectivePrice(p.SalePrice, p.PromoType, p.PromoPrice, p.PromoRate,
                    p.PromoEnabled, p.PromoStartAt, p.PromoEndAt, now);
                var subTotal = Math.Round(item.Qty * unitPrice, 2);
                detailList.Add((p, item.Qty, unitPrice, p.SalePrice, subTotal));
                listAmount += Math.Round(item.Qty * p.SalePrice, 2);
                dealAmount += subTotal;
            }
            listAmount = Math.Round(listAmount, 2);
            dealAmount = Math.Round(dealAmount, 2);
            // 促销让利 = 原价合计 − 成交合计（PromoHelper 已保证促销价不高于挂牌价，故不会为负）
            var promoSaving = Math.Round(listAmount - dealAmount, 2);
            if (promoSaving < 0) promoSaving = 0;

            // 2. 整单优惠：两种行业录入方式（按折率 / 按金额），落库以金额为准。
            //    折率作用在「成交合计」上（档案促销已经打过折了，整单折是在折后价上再让），
            //    所以这里的上限是 dealAmount 而不是商品总额。
            //    ① 按折率：先四舍五入折后金额，再减法反算优惠额——顺序不能反。
            //       正算 round(总额 ×(1−折率)) 与反算会差 1 分：整数折与 x.4/x.8 折会让乘积正好落到
            //       「半分」中点上，而 Math.Round 默认银行家舍入在两条路径上会落到不同方向
            //       （实测 总额 1.00~999.99 区间：5 折有 50%、1/3/7/9 折各有 10% 的金额差 1 分）。
            //       减法反算能保证「总额 − 优惠 = 折后 = 应收基数」恒成立，列表合计与导出对得上。
            //    ② 按金额：直接取前端传入值。
            //    两者都传时以折率为准——折率可独立重算校验，金额无法校验。
            var discountRate = dto.DiscountRate.HasValue ? Math.Round(dto.DiscountRate.Value, 2) : (decimal?)null;
            decimal orderDiscount;
            if (discountRate.HasValue)
            {
                if (discountRate.Value < 0.1m || discountRate.Value > 10m)
                    return ApiResult<object>.Fail("折扣需在 0.1 ~ 10 折之间");
                var afterByRate = Math.Round(dealAmount * discountRate.Value / 10m, 2);
                orderDiscount = Math.Round(dealAmount - afterByRate, 2);
            }
            else
            {
                orderDiscount = Math.Round(dto.DiscountAmount, 2);
            }
            if (orderDiscount < 0) orderDiscount = 0;
            if (orderDiscount > dealAmount) orderDiscount = dealAmount;

            // 3. 优惠金额 = 档案促销让利 + 整单优惠（单据上「优惠」这一行的数）
            var discountAmount = Math.Round(promoSaving + orderDiscount, 2);

            // 4. 折后金额 = 商品总额 − 优惠金额 = 成交合计 − 整单优惠
            var afterDiscount = Math.Round(listAmount - discountAmount, 2);
            if (afterDiscount < 0) afterDiscount = 0;

            // 5. 抹零金额：行业惯例只对现金抹零，且不超过折后金额。
            //    混合支付不设抹零入口（dto.Payments 非空即强制 0）：抹零是「现金找零不方便」的产物，
            //    混合模式的金额是各行凑出来的，抹零会让「各行之和 = 应收 + 找零」这条恒等式失去落点。
            var isMixed = dto.Payments is { Count: > 0 };
            var isCash = !isMixed && dto.PayMethod == "现金";
            var roundOffAmount = isCash ? Math.Round(dto.RoundOffAmount, 2) : 0m;
            if (roundOffAmount < 0) roundOffAmount = 0;
            if (roundOffAmount > afterDiscount) roundOffAmount = afterDiscount;

            // 6. 应收金额 = 折后金额 − 抹零金额（顾客应付）
            var payAmount = Math.Round(afterDiscount - roundOffAmount, 2);
            if (payAmount < 0) payAmount = 0;

            // 7~9. 收款方式 / 收款额 / 找零 / 实收 / 挂账额：单项与混合两条路，最后落成同一组字段
            string payMethod;
            decimal cashAmount, changeAmount, receivedAmount, creditAmount;   // creditAmount = 挂账额（没收到的那部分）
            // 落库的支付组成：≥2 行才写 SaleOrderPayments（单项支付不落行，收款方式一个字段就说清了）
            var paymentRows = new List<(string Method, decimal Amount)>();

            if (isMixed)
            {
                // ===== 混合支付 =====
                // 先按支付方式合并（同一方式多行相加）、剔除金额为 0 的行
                var byMethod = new Dictionary<string, decimal>();
                foreach (var p in dto.Payments!)
                {
                    var m = (p.PayMethod ?? "").Trim();
                    if (m.Length == 0) return ApiResult<object>.Fail("支付方式不能为空");
                    if (!SupportedPayMethods.Contains(m)) return ApiResult<object>.Fail($"不支持的支付方式：{m}");
                    var amt = Math.Round(p.Amount, 2);
                    if (amt < 0) return ApiResult<object>.Fail($"「{m}」的收款金额不能为负数");
                    if (amt == 0) continue;
                    byMethod[m] = byMethod.TryGetValue(m, out var old) ? old + amt : amt;
                }
                if (byMethod.Count == 0) return ApiResult<object>.Fail("请填写各支付方式的收款金额");

                // 「赊账」行不是收到的钱，单独拎出来；剩下各行才是实收
                var declaredCredit = byMethod.TryGetValue(CreditPayMethod, out var dc) ? dc : 0m;
                byMethod.Remove(CreditPayMethod);
                var cashPart = byMethod.TryGetValue("现金", out var cp) ? cp : 0m;
                var paidTotal = Math.Round(byMethod.Values.Sum(), 2);          // 实收合计（不含挂账）
                var cashlessTotal = Math.Round(paidTotal - cashPart, 2);       // 非现金实收合计

                // 找零只能来自现金：非现金部分超过应收，等于「多扫了一笔微信又把现金找回去」，不合业务
                if (cashlessTotal > payAmount)
                    return ApiResult<object>.Fail($"非现金收款合计 ¥{cashlessTotal} 不能超过应收金额 ¥{payAmount}");
                changeAmount = Math.Max(0, Math.Round(paidTotal - payAmount, 2));

                // 差额挂账：实收不足的部分自动转成欠款（「付一部分 + 差额挂账」就是这条路径）。
                // 显式填了「赊账」行时以该行为准，并要求与差额一致——否则「已收 + 挂账」对不上应收，
                // 等于把差额凭空吞掉，账面上看不出来。
                var shortfall = Math.Round(payAmount - paidTotal, 2);
                if (declaredCredit > 0 && shortfall <= 0)
                    return ApiResult<object>.Fail($"已收 ¥{paidTotal} 已达到应收 ¥{payAmount}，无需再挂账");
                creditAmount = shortfall > 0 ? shortfall : 0m;
                if (declaredCredit > 0 && declaredCredit != creditAmount)
                    return ApiResult<object>.Fail($"支付金额与应收不符：已收 ¥{paidTotal} + 挂账 ¥{declaredCredit} ≠ 应收 ¥{payAmount}");
                if (creditAmount > 0 && !saleCfg.AllowCredit)
                    return ApiResult<object>.Fail("系统设置不允许赊账，请更换收款方式");

                cashAmount = Math.Round(cashPart, 2);                          // 现金行填的是递钞额，可大于应收
                receivedAmount = Math.Round(paidTotal - changeAmount, 2);      // 实收 = 收到的钱 − 找零

                foreach (var kv in byMethod) paymentRows.Add((kv.Key, Math.Round(kv.Value, 2)));
                if (creditAmount > 0) paymentRows.Add((CreditPayMethod, creditAmount));

                // 落库口径：只剩一种方式且没挂账就记那一种（与单项支付一致），否则记「混合」
                payMethod = paymentRows.Count >= 2 ? MixedPayMethod : paymentRows[0].Method;
            }
            else
            {
                // ===== 单项支付（原逻辑，一字未改） =====
                payMethod = dto.PayMethod;
                cashAmount = isCash ? Math.Round(dto.CashAmount, 2) : 0m;
                if (isCash && cashAmount < payAmount)
                    return ApiResult<object>.Fail($"收款金额不足（应收 ¥{payAmount}）");
                changeAmount = isCash ? Math.Round(cashAmount - payAmount, 2) : 0m;
                if (changeAmount < 0) changeAmount = 0;

                // 实收金额 = 实际收到的净额：现金/微信/支付宝即应收；赊账为挂账，开单时实收 0
                // （后续每笔还款累加到该单实收上，见 SaleService.Credit.SettleAsync）
                creditAmount = dto.IsCredit ? payAmount : 0m;
                receivedAmount = dto.IsCredit ? 0m : payAmount;
            }

            // 是否赊账由「挂账额」决定：单项赊账=全额应收，混合支付=差额（第 196 行已把 PayMethod=赊账单的 IsCredit 置位）
            var isCredit = creditAmount > 0;

            var so = new SaleOrder
            {
                OrderNo = orderNo,
                TotalAmount = listAmount,   // 商品总额＝原价合计（未优惠前）
                DiscountAmount = discountAmount,
                DiscountRate = discountRate,
                RoundOffAmount = roundOffAmount,
                PayAmount = payAmount,
                ReceivedAmount = receivedAmount,
                PayMethod = payMethod,
                CashAmount = cashAmount,
                ChangeAmount = changeAmount,
                IsCredit = isCredit,
                WechatId = isCredit ? (string.IsNullOrWhiteSpace(dto.WechatId) ? null : dto.WechatId!.Trim()) : null,
                Remark = dto.Remark,
                CreatedBy = _me.Id, CreatedAt = DateTime.Now,
            };
            _db.SaleOrders.Add(so);
            await _db.SaveChangesAsync();

            // 混合支付落支付明细（一行一个方式）。恒等式：Σ Amount = 应收 + 找零
            if (paymentRows.Count >= 2)
            {
                foreach (var (method, amount) in paymentRows)
                    _db.SaleOrderPayments.Add(new SaleOrderPayment
                    { OrderId = so.Id, PayMethod = method, Amount = amount });
            }

            foreach (var (p, qty, unitPrice, originalPrice, subTotal) in detailList)
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
                    OriginalPrice = originalPrice,   // 挂牌价快照：档案优惠日后再改，历史单据靠它才能解释当时让了多少
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

            // 赊账欠款记录：欠款额 = 挂账额（单项赊账即全额应收，混合支付是「付剩下的差额」）
            if (isCredit)
            {
                _db.CreditSales.Add(new CreditSale
                {
                    SaleOrderId = so.Id, WechatId = string.IsNullOrWhiteSpace(dto.WechatId) ? "" : dto.WechatId!.Trim(),
                    Phone = string.IsNullOrWhiteSpace(dto.Phone) ? null : dto.Phone!.Trim(),
                    Remark = string.IsNullOrWhiteSpace(dto.Remark) ? null : dto.Remark!.Trim(),
                    CreditAmount = creditAmount, PaidAmount = 0, RemainingAmount = creditAmount,
                    Status = false, CreatedAt = so.CreatedAt,
                });
            }

            await _db.SaveChangesAsync();
            _db.OperationLogs.Add(new OperationLog
            {
                UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp, Module = "销售管理",
                Action = "收银结算",
                // 混合支付把每一行的方式与金额都写进日志：事后查一笔钱收在哪，不能只看到「混合」两个字。
                // 按折率录入的另外记一笔折数，便于区分「按折扣让利」与「手工抹了个数」；
                // 让利超限的补一笔授权说明 —— 事后要能看出这单的让利是「常规额度内」还是「谁授权放行的」。
                Target = $"{orderNo} 应收 ¥{so.PayAmount}，实收 ¥{so.ReceivedAmount}（{PayComposition(payMethod, paymentRows)}）"
                         + (discountRate.HasValue ? $"，折扣 {discountRate.Value:0.##} 折" : "")
                         + (fence.AuthorizedNote is { } authNote ? $"，超额让利已授权（{authNote}）" : ""),
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

    /// <summary>销售单支付明细 / 收款方式的取值约定，见 <see cref="SaleOrderPayment"/> 与 <see cref="SaleOrder.PayMethod"/>。</summary>
    private const string CreditPayMethod = "赊账";
    private const string MixedPayMethod = "混合";
    private static readonly HashSet<string> SupportedPayMethods = new() { "现金", "微信", "支付宝", CreditPayMethod };

    /// <summary>日志里的收款口径：混合支付展开成「混合：现金 ¥10.00 + 赊账 ¥7.00」，单项支付就写方式名</summary>
    private static string PayComposition(string payMethod, List<(string Method, decimal Amount)> rows) =>
        rows.Count >= 2
            ? $"{MixedPayMethod}：" + string.Join(" + ", rows.Select(r => $"{r.Method} ¥{r.Amount:0.00}"))
            : payMethod;

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
                Target = $"{o.OrderNo} 应收 ¥{o.PayAmount}，实收 ¥{o.ReceivedAmount}（{o.PayMethod}），回补库存 {details.Sum(d => d.Quantity)} 件",
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
