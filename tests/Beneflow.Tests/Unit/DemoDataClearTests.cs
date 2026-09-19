using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 「初始化数据」（清空演示数据）的单元测试。守住三件事：
/// ① 确认文字不对就绝不动手；② 业务数据一张表都不落下地清干净；
/// ③ 账号 / 角色 / 菜单 / 系统设置必须原样保留（这是用户明确要求的边界）。
///
/// 说明：清空的删除顺序（子表 → 主表）在 InMemory 上**测不出来** —— 它不校验外键，
/// 顺序写反也照样通过。这条只能靠 SQL Server 上的真实验证兜（见工作日志）。
/// </summary>
public class DemoDataClearTests : TestBase
{
    /// <summary>16 张业务表各塞一行，用来验证「一张都没落下」；顺手塞一条子分类验证自引用也能清掉。</summary>
    private void SeedBusinessRows()
    {
        var now = DateTime.Now;
        // 子分类（ParentId 指向已有分类）：清空时若先删父分类，SQL Server 上会撞外键
        Db.Categories.Add(new ProductCategory { Name = "子分类", ParentId = CategoryId, Sort = 2 });
        Db.Batches.Add(new ProductBatch { ProductId = ProductAId, BatchNo = "B2601010001", ExpireDate = now.AddDays(300), Quantity = 5 });

        Db.StockLogs.Add(new StockLog
        {
            ProductId = ProductAId, ChangeType = "采购入库", ChangeQty = 5, BeforeQty = 0, AfterQty = 5,
            RefNo = "PO20260919001", CreatedBy = UserId, CreatedAt = now,
        });

        var po = new PurchaseOrder { OrderNo = "PO20260919001", SupplierId = SupplierId, TotalQty = 5, TotalAmount = 25m, CreatedBy = UserId, CreatedAt = now };
        Db.PurchaseOrders.Add(po);
        Db.SaveChanges();
        Db.PurchaseOrderDetails.Add(new PurchaseOrderDetail { OrderId = po.Id, ProductId = ProductAId, Qty = 5, CostPrice = 5m, SubTotal = 25m });

        var pr = new PurchaseReturn { OrderNo = "PR20260919001", SupplierId = SupplierId, RefundAmount = 25m, CreatedBy = UserId, CreatedAt = now };
        Db.PurchaseReturns.Add(pr);
        Db.SaveChanges();
        Db.PurchaseReturnDetails.Add(new PurchaseReturnDetail { ReturnId = pr.Id, ProductId = ProductAId, Qty = 5, CostPrice = 5m, SubTotal = 25m });

        var sale = SeedSale(10m, now);
        var saleDetail = SeedSaleDetail(sale, ProductAId, 1m, 10m, 5m);

        var sr = new SaleReturn { OrderNo = "SR20260919001", SaleOrderId = sale.Id, RefundAmount = 10m, CreatedBy = UserId, CreatedAt = now };
        Db.SaleReturns.Add(sr);
        Db.SaveChanges();
        Db.SaleReturnDetails.Add(new SaleReturnDetail
        {
            ReturnId = sr.Id, SaleOrderDetailId = saleDetail.Id, ProductId = ProductAId,
            ProductName = "商品A（无有效期）", Qty = 1m, UnitPrice = 10m, SubTotal = 10m,
        });

        var credit = new CreditSale { SaleOrderId = sale.Id, WechatId = "wx_demo", CreditAmount = 10m, RemainingAmount = 10m, CreatedAt = now };
        Db.CreditSales.Add(credit);
        Db.SaveChanges();
        Db.CreditPayments.Add(new CreditPayment { CreditSaleId = credit.Id, PayAmount = 5m, CreatedBy = UserId, CreatedAt = now });

        var check = new StockCheck { OrderNo = "SC20260919001", CreatedBy = UserId, CreatedAt = now };
        Db.StockChecks.Add(check);
        Db.SaveChanges();
        Db.StockCheckDetails.Add(new StockCheckDetail { CheckId = check.Id, ProductId = ProductAId, BookQty = 5m, ActualQty = 4m, DiffQty = -1m });

        Db.OperationLogs.Add(new OperationLog { UserId = UserId, UserName = "admin", Module = "销售管理", Action = "收银开单", CreatedAt = now });
        Db.LoginLogs.Add(new LoginLog { UserId = UserId, UserName = "admin", Success = true, CreatedAt = now });
        Db.SaveChanges();
    }

    /// <summary>业务数据是否已全部清空（表名与断言一起给出，失败时能直接看出是哪张表）。
    /// 日志两张表不在这里断言：清空后必须留一条「初始化数据」的操作日志，单独判断。</summary>
    private void AssertBusinessDataCleared()
    {
        Assert.Empty(Db.CreditPayments);
        Assert.Empty(Db.CreditSales);
        Assert.Empty(Db.SaleReturnDetails);
        Assert.Empty(Db.SaleReturns);
        Assert.Empty(Db.SaleOrderDetails);
        Assert.Empty(Db.SaleOrders);
        Assert.Empty(Db.PurchaseReturnDetails);
        Assert.Empty(Db.PurchaseReturns);
        Assert.Empty(Db.PurchaseOrderDetails);
        Assert.Empty(Db.PurchaseOrders);
        Assert.Empty(Db.StockCheckDetails);
        Assert.Empty(Db.StockChecks);
        Assert.Empty(Db.StockLogs);
        Assert.Empty(Db.Batches);
        Assert.Empty(Db.Products);
        Assert.Empty(Db.Categories);
        Assert.Empty(Db.Suppliers);
    }

    [Fact]
    public async Task 没有标记时视为仍是演示数据()
    {
        // 本功能上线前播种的老库没有 demo 行，必须照样提示，否则用户看不到初始化入口
        Assert.True(await SettingSvc.IsDemoDataAsync());

        // 坏值也不能把提示吞掉
        SetConfig("demo", "not-a-json");
        Assert.True(await SettingSvc.IsDemoDataAsync());
    }

    [Fact]
    public async Task 标记为已初始化后不再视为演示数据()
    {
        SetConfig("demo", "{\"isDemoData\":false}");
        Assert.False(await SettingSvc.IsDemoDataAsync());
    }

    [Fact]
    public async Task 确认文字不对时不清空也不改标记()
    {
        SeedBusinessRows();

        var wrong = new[] { null, "", "清空吧", " 清 空 " };
        foreach (var text in wrong)
        {
            var r = await SettingSvc.ClearDemoDataAsync(text);
            Assert.NotEqual(0, r.Code);
            Assert.Contains("清空", r.Message);
        }

        // 一行都不许少，标记也必须还是「演示数据」
        Assert.NotEmpty(Db.Products);
        Assert.NotEmpty(Db.SaleOrders);
        Assert.NotEmpty(Db.PurchaseOrders);
        Assert.True(await SettingSvc.IsDemoDataAsync());
    }

    [Fact]
    public async Task 清空业务数据并保留账号角色菜单与系统设置()
    {
        SeedBusinessRows();
        SetConfig("shop", "{\"name\":\"百惠通便利店\"}");
        var usersBefore = await Db.Users.CountAsync();
        var rolesBefore = await Db.Roles.CountAsync();
        var userRolesBefore = await Db.UserRoles.CountAsync();
        var menusBefore = await Db.Menus.CountAsync();
        var roleMenusBefore = await Db.RoleMenus.CountAsync();

        var r = await SettingSvc.ClearDemoDataAsync("清空");

        Assert.Equal(0, r.Code);

        // ① 业务数据全清
        AssertBusinessDataCleared();

        // ② 系统侧数据一个都不能少（用户明确要求「用户角色权限那些别清」）
        Assert.Equal(usersBefore, await Db.Users.CountAsync());
        Assert.Equal(rolesBefore, await Db.Roles.CountAsync());
        Assert.Equal(userRolesBefore, await Db.UserRoles.CountAsync());
        Assert.Equal(menusBefore, await Db.Menus.CountAsync());
        Assert.Equal(roleMenusBefore, await Db.RoleMenus.CountAsync());
        Assert.NotNull(await Db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == "shop"));

        // ③ 标记翻转：前端常驻提示据此消失
        Assert.False(await SettingSvc.IsDemoDataAsync());

        // ④ 日志：演示期的登录/操作痕迹清掉，但「初始化数据」这一条要留下 ——
        //    所以它必须写在清空日志之后，否则会被自己删掉
        Assert.Empty(Db.LoginLogs);
        var leftLogs = await Db.OperationLogs.AsNoTracking().ToListAsync();
        Assert.All(leftLogs, l => Assert.Equal("初始化数据", l.Action));
        var log = leftLogs.FirstOrDefault(l => l.Action == "初始化数据");
        Assert.NotNull(log);
        Assert.Equal("系统管理", log!.Module);
    }

    [Fact]
    public async Task 非关系库跳过物理备份且不算失败()
    {
        // InMemory 没有 BACKUP DATABASE 能力：既不报错、也不假报一个备份路径
        var r = await SettingSvc.ClearDemoDataAsync("清空");

        Assert.Equal(0, r.Code);
        Assert.Null(r.Data!.BackupPath);
        Assert.Null(r.Data.BackupError);
    }

    [Fact]
    public async Task 清空后原账号密码仍可登录()
    {
        SeedBusinessRows();

        await SettingSvc.ClearDemoDataAsync("清空");

        // 清的是业务数据，账号/密码/角色关联都不能被动到：否则用户初始化完就把自己锁在门外了
        var login = await AuthSvc.LoginAsync("admin", "123456", "127.0.0.1");
        Assert.Equal(0, login.Code);
    }
}
