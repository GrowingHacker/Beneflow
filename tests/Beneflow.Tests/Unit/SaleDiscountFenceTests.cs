using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 收银台让利风控：限额 → 授权 → 留痕。
///
/// 要守住的口径（对着 SaleService.Discount.cs 的三条设计决定写）：
/// ① 限额来自 sale 配置组，缺项/坏值一律回退默认（整单优惠 ≤ ¥50、不低于 9 折、抹零 ≤ ¥1），
///    越界配置夹回合法区间 —— 配置写坏不能让收银台瘫掉；
/// ② 授权 = 「当前账号持 owner 角色 + 该账号密码」两件都对，缺一件都拒；
/// ③ 让利是按「实际生效的那一项」判的：有折率时只比折率（折率为准）；
///    抹零只在「单项支付 + 现金」才生效，混合支付与非现金单超限也不该索要授权。
///
/// 注意：测试跑 InMemory，事务是 no-op，所以「被拒的日志写在事务之外、不会被回滚」这一条
/// 在这里证不了 —— 能证的是「被拒时零写入 + 日志两条都在」。真事务下的回滚语义要真 SQL Server。
/// </summary>
public class SaleDiscountFenceTests : TestBase
{
    private static SaleItemDto Item(int productId, decimal qty) => new() { ProductId = productId, Qty = qty };
    private static SalePaymentDto Pay(string method, decimal amount) => new() { PayMethod = method, Amount = amount };

    /// <summary>构造一张销售单请求（默认微信、1 件商品A = 应收 ¥10）</summary>
    private CreateSaleDto Sale(decimal qty = 1, string payMethod = "微信", decimal cash = 0,
        decimal discount = 0, decimal? rate = null, decimal roundOff = 0,
        List<SalePaymentDto>? payments = null, string? authPwd = null) => new()
    {
        Items = new List<SaleItemDto> { Item(ProductAId, qty) },
        PayMethod = payMethod, CashAmount = cash, DiscountAmount = discount, DiscountRate = rate,
        RoundOffAmount = roundOff, Payments = payments, DiscountAuthPassword = authPwd,
    };

    /// <summary>把当前登录人换成收银员（新建账号 + 指定角色，并把 CurrentUser 指过去）</summary>
    private void LoginAsCashier()
    {
        var salt = PasswordHasher.NewSalt();
        var cashier = new UserInfo { Username = "cashier1", Name = "收银员一号", Salt = salt, Status = true };
        cashier.PasswordHash = PasswordHasher.Hash("123456", salt);
        Db.Users.Add(cashier);
        Db.SaveChanges();
        Db.UserRoles.Add(new UserRole { UserId = cashier.Id, RoleId = CashierRoleId });
        Db.SaveChanges();

        CurrentUser.Id = cashier.Id;
        CurrentUser.Username = cashier.Username;
    }

    private int LogCount(string action) =>
        Db.OperationLogs.AsNoTracking().Count(l => l.Module == "收银台" && l.Action == action);

    private async Task<OperationLog> LastLog() =>
        await Db.OperationLogs.AsNoTracking().OrderByDescending(l => l.Id).FirstAsync();

    // ==================== 常规额度：不打扰收银员 ====================

    [Fact]
    public async Task CreateAsync_抹零在限额内_无需授权()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 0.5m));

        Assert.Equal(0, r.Code);
        Assert.Equal(0, LogCount("超额让利被拒"));
        Assert.DoesNotContain("超额让利已授权", (await LastLog()).Target);
    }

    [Fact]
    public async Task CreateAsync_抹零正好等于上限_无需授权()
    {
        SetStock(ProductAId, 10);

        // 边界：限额是「超过才拦」，等于上限属于常规额度（¥1 = 抹到元为止的全部零头）
        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 1m));

        Assert.Equal(0, r.Code);
    }

    [Fact]
    public async Task CreateAsync_九折_无需授权()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(1, rate: 9));

        Assert.Equal(0, r.Code);   // 9 折正好是下限
    }

    // ==================== 超限 + 未授权：拒绝并留痕 ====================

    [Fact]
    public async Task CreateAsync_抹零超限_无密码_被拒并留痕()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 3m));

        Assert.NotEqual(0, r.Code);
        Assert.Contains("让利超出限额", r.Message);
        Assert.Contains("抹零 ¥3.00 超过上限 ¥1.00", r.Message);
        Assert.Contains("未提供店主授权密码", r.Message);

        // 越权尝试必须留痕，且业务零写入（拦在事务之前，不留半张单、不动库存）
        var log = await LastLog();
        Assert.Equal("收银台", log.Module);
        Assert.Equal("超额让利被拒", log.Action);
        Assert.Contains("抹零 ¥3.00 超过上限 ¥1.00", log.Target);
        Assert.Empty(Db.SaleOrders);
        Assert.Equal(10m, GetProduct(ProductAId).StockQuantity);
    }

    [Fact]
    public async Task CreateAsync_抹零超限_密码错误_被拒并留痕()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 3m, authPwd: "wrong-pwd"));

        Assert.NotEqual(0, r.Code);
        Assert.Contains("店主授权密码不正确", r.Message);
        Assert.Equal(1, LogCount("超额让利被拒"));
        Assert.Empty(Db.SaleOrders);
    }

    [Fact]
    public async Task CreateAsync_整单优惠超限_无密码_被拒()
    {
        SetStock(ProductAId, 10);

        // 手搓请求：前端没有任何整单优惠入口，但接口收这个字段
        var r = await SaleSvc.CreateAsync(Sale(2, discount: 999));

        Assert.NotEqual(0, r.Code);
        Assert.Contains("整单优惠 ¥999.00 超过上限 ¥50.00", r.Message);
        Assert.Equal(1, LogCount("超额让利被拒"));
        Assert.Empty(Db.SaleOrders);
    }

    [Fact]
    public async Task CreateAsync_折率低于下限_无密码_被拒()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(1, rate: 0.5m));

        Assert.NotEqual(0, r.Code);
        Assert.Contains("折率 0.5 折低于下限 9 折", r.Message);
        Assert.Equal(1, LogCount("超额让利被拒"));
        Assert.Empty(Db.SaleOrders);
    }

    [Fact]
    public async Task CreateAsync_非店主账号_密码正确也拒绝()
    {
        SetStock(ProductAId, 10);
        LoginAsCashier();

        // 收银员账号本身就没有让利能力，密码填什么都不管用 —— 权限的问题用权限解决，不用口令硬掰
        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 3m, authPwd: "123456"));

        Assert.NotEqual(0, r.Code);
        Assert.Contains("不是店主", r.Message);
        Assert.Equal(1, LogCount("超额让利被拒"));
        Assert.Empty(Db.SaleOrders);
    }

    // ==================== 超限 + 店主授权：放行并留痕 ====================

    [Fact]
    public async Task CreateAsync_抹零超限_店主授权_放行且日志带授权说明与单号()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 3m, authPwd: "123456"));

        Assert.Equal(0, r.Code);
        var orderNo = GetResultDataProp<string>(r.Data!, "orderNo")!;

        // 抹零 3 照旧生效：授权只是「允许按这个数让利」，不改金额链
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync(x => x.OrderNo == orderNo);
        Assert.Equal(3m, o.RoundOffAmount);
        Assert.Equal(17m, o.PayAmount);

        // 留痕落在收银结算那条日志上，带单号、带授权人 —— 事后看得出这单的让利是常规还是有人放行的
        var log = await LastLog();
        Assert.Equal("收银结算", log.Action);
        Assert.Contains(orderNo, log.Target);
        Assert.Contains("超额让利已授权", log.Target);
        Assert.Contains("抹零 ¥3.00 超过上限 ¥1.00", log.Target);
        Assert.Contains("店主 admin 授权", log.Target);
        Assert.Equal(0, LogCount("超额让利被拒"));   // 授权通过不是越权
    }

    [Fact]
    public async Task CreateAsync_整单优惠超限_店主授权_放行()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(2, discount: 999, authPwd: "123456"));

        Assert.Equal(0, r.Code);
        // 授权放行之后仍然受「优惠不超过总额」这条算术约束，夹到 20（授权不等于取消所有校验）
        Assert.Equal(20m, (await Db.SaleOrders.AsNoTracking().FirstAsync()).DiscountAmount);
    }

    [Fact]
    public async Task CreateAsync_折率超限_店主授权_放行并留折率()
    {
        SetStock(ProductAId, 10);

        var r = await SaleSvc.CreateAsync(Sale(1, rate: 0.1m, authPwd: "123456"));

        Assert.Equal(0, r.Code);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync();
        Assert.Equal(0.1m, o.DiscountRate);
        Assert.Contains("超额让利已授权", (await LastLog()).Target);
    }

    // ==================== 判定口径与实际生效项对齐 ====================

    [Fact]
    public async Task CreateAsync_有折率时_只比折率不比金额()
    {
        SetStock(ProductAId, 10);

        // 折率与金额是同一件事的两种录入方式，业务上以折率为准（见 CreateCoreAsync），
        // 所以金额字段填多大都不参与判定 —— 若这里改成「金额也拦」，就会对一单本来只让 1 折的钱索要授权
        var r = await SaleSvc.CreateAsync(Sale(1, discount: 9999, rate: 9.5m));

        Assert.Equal(0, r.Code);
        Assert.Equal(0, LogCount("超额让利被拒"));
    }

    [Fact]
    public async Task CreateAsync_非现金单抹零超限_不索要授权()
    {
        SetStock(ProductAId, 10);

        // 抹零只有「单项支付 + 现金」才真的减钱：非现金单的抹零会被忽略，那就不该惊动店主
        var r = await SaleSvc.CreateAsync(Sale(2, "微信", roundOff: 999));

        Assert.Equal(0, r.Code);
        var o = await Db.SaleOrders.AsNoTracking().FirstAsync();
        Assert.Equal(0m, o.RoundOffAmount);
        Assert.Equal(20m, o.PayAmount);
        Assert.Equal(0, LogCount("超额让利被拒"));
    }

    [Fact]
    public async Task CreateAsync_混合支付抹零超限_不索要授权()
    {
        SetStock(ProductAId, 10);

        // 混合支付恒不抹零，同上
        var r = await SaleSvc.CreateAsync(Sale(2, payments: new List<SalePaymentDto> { Pay("现金", 20m) }, roundOff: 999));

        Assert.Equal(0, r.Code);
        Assert.Equal(0m, (await Db.SaleOrders.AsNoTracking().FirstAsync()).RoundOffAmount);
        Assert.Equal(0, LogCount("超额让利被拒"));
    }

    // ==================== 限额本身是配置 ====================

    [Fact]
    public async Task CreateAsync_限额可调高_原来的超额变成常规()
    {
        SetStock(ProductAId, 10);
        SetConfig("sale", """{"allowCredit":true,"maxOrderDiscount":50,"minDiscountRate":9,"maxRoundOff":5}""");

        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 3m));

        Assert.Equal(0, r.Code);
        Assert.Equal(0, LogCount("超额让利被拒"));
    }

    [Fact]
    public async Task CreateAsync_限额可调低_原来的常规变成超额()
    {
        SetStock(ProductAId, 10);
        SetConfig("sale", """{"allowCredit":true,"maxOrderDiscount":50,"minDiscountRate":9,"maxRoundOff":0}""");

        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 0.5m));

        Assert.NotEqual(0, r.Code);
        Assert.Contains("抹零 ¥0.50 超过上限 ¥0.00", r.Message);
    }

    [Fact]
    public async Task CreateAsync_老库缺限额键_回退默认值()
    {
        SetStock(ProductAId, 10);

        // 本功能上线前保存过的 sale 组只有两个键 —— 此时按默认限额走（抹零上限 ¥1）
        SetConfig("sale", """{"allowCredit":true,"defaultPayMethod":"现金"}""");

        Assert.Equal(0, (await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 1m))).Code);
        Assert.NotEqual(0, (await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 1.5m))).Code);
    }

    [Fact]
    public async Task CreateAsync_限额值为非数字_回退默认值()
    {
        SetStock(ProductAId, 10);

        // 配置写坏不能把收银台卡死：认不出来的值一律按默认限额办
        SetConfig("sale", """{"maxOrderDiscount":"随便填的","minDiscountRate":null,"maxRoundOff":""}""");

        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 1m));

        Assert.Equal(0, r.Code);
    }

    [Fact]
    public async Task CreateAsync_限额越界_夹到合法区间()
    {
        SetStock(ProductAId, 10);

        // 折率下限填 0 ⇒ 夹到 0.1；负的抹零上限 ⇒ 夹到 0（不是「负数 ⇒ 永远超限」那种无解状态）
        SetConfig("sale", """{"maxOrderDiscount":-5,"minDiscountRate":0,"maxRoundOff":-1}""");

        // 0.1 折正好等于夹紧后的下限，放行
        Assert.Equal(0, (await SaleSvc.CreateAsync(Sale(1, rate: 0.1m))).Code);

        // 抹零上限被夹到 0 ⇒ 抹 0.01 也算超额，但仍是个「给密码就能过」的状态，不会锁死
        var r = await SaleSvc.CreateAsync(Sale(2, "现金", cash: 20, roundOff: 0.01m, authPwd: "123456"));
        Assert.Equal(0, r.Code);
    }
}
