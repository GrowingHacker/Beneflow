using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>操作日志测试：写入内容、关键字/时间筛选、分页边界</summary>
public class LogServiceTests : TestBase
{
    /// <summary>直接落库一条日志（绕过业务服务，便于精确控制时间）</summary>
    private OperationLog SeedLog(string module, string action, string? target, DateTime at, string user = "admin")
    {
        var l = new OperationLog
        {
            UserId = UserId, UserName = user, IpAddress = "127.0.0.1",
            Module = module, Action = action, Target = target, CreatedAt = at,
        };
        Db.OperationLogs.Add(l);
        Db.SaveChanges();
        return l;
    }

    private async Task<PagedResult<object>> QueryAsync(string? keyword = null, DateTime? from = null,
        DateTime? to = null, int page = 1, int pageSize = 20) =>
        await LogSvc.QueryAsync(keyword, from, to, page, pageSize);

    [Fact]
    public async Task WriteAsync_UsesCurrentUserAndIsPersistedOnSave()
    {
        await LogSvc.WriteAsync("商品管理", "新增商品", "可乐");

        // WriteAsync 只入上下文，不落库
        Assert.Equal(0, await Db.OperationLogs.CountAsync());

        await Db.SaveChangesAsync();

        var log = await Db.OperationLogs.AsNoTracking().SingleAsync();
        Assert.Equal("商品管理", log.Module);
        Assert.Equal("新增商品", log.Action);
        Assert.Equal("可乐", log.Target);
        Assert.Equal(CurrentUser.Username, log.UserName);
        Assert.Equal("127.0.0.1", log.IpAddress);
        Assert.Equal(CurrentUser.Id, log.UserId);
    }

    [Fact]
    public async Task QueryAsync_NoData_ReturnsEmptyPage()
    {
        var page = await QueryAsync();

        Assert.Equal(0, page.Total);
        Assert.Empty(page.List);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
    }

    [Fact]
    public async Task QueryAsync_OrdersByNewestFirst()
    {
        SeedLog("商品管理", "新增商品", "A", DateTime.Today.AddHours(9));
        SeedLog("销售管理", "收银", "B", DateTime.Today.AddHours(10));

        var page = await QueryAsync();

        Assert.Equal(2, page.Total);
        Assert.Equal("收银", Prop<string>(page.List[0], "action"));
        Assert.Equal("新增商品", Prop<string>(page.List[1], "action"));
    }

    [Theory]
    [InlineData("可乐", "target")]
    [InlineData("admin", "userName")]
    [InlineData("销售", "module")]
    [InlineData("收银", "action")]
    public async Task QueryAsync_KeywordMatchesEachField(string keyword, string expectedField)
    {
        SeedLog("销售管理", "收银", "可乐", DateTime.Today.AddHours(9), "admin");
        SeedLog("商品管理", "新增商品", "雪碧", DateTime.Today.AddHours(10), "cashier");

        var page = await QueryAsync(keyword);

        Assert.Single(page.List);
        Assert.NotNull(Prop<string>(page.List[0], expectedField));
    }

    [Fact]
    public async Task QueryAsync_KeywordNoMatch_ReturnsEmpty()
    {
        SeedLog("销售管理", "收银", "可乐", DateTime.Today);

        var page = await QueryAsync("不存在的关键词");

        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task QueryAsync_DateRange_InclusiveOnBothEnds()
    {
        var d1 = new DateTime(2026, 1, 10);
        SeedLog("销售管理", "收银1", null, d1.AddHours(8));              // 当天 08:00
        SeedLog("销售管理", "收银2", null, d1.AddDays(2).AddHours(8));   // 第三天
        SeedLog("销售管理", "收银3", null, d1.AddDays(5).AddHours(8));   // 范围外

        var page = await QueryAsync(null, d1, d1.AddDays(2));

        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task QueryAsync_OnlyFrom_FiltersEarlier()
    {
        var d1 = new DateTime(2026, 3, 1);
        SeedLog("销售管理", "旧", null, d1.AddDays(-1));
        SeedLog("销售管理", "新", null, d1.AddHours(12));

        var page = await QueryAsync(null, d1);

        Assert.Single(page.List);
        Assert.Equal("新", Prop<string>(page.List[0], "action"));
    }

    [Fact]
    public async Task QueryAsync_Paging_SkipsAndCaps()
    {
        for (var i = 1; i <= 5; i++)
            SeedLog("销售管理", $"动作{i}", null, DateTime.Today.AddHours(i));

        var p1 = await QueryAsync(null, null, null, 1, 2);
        var p2 = await QueryAsync(null, null, null, 3, 2);

        Assert.Equal(5, p1.Total);
        Assert.Equal(2, p1.List.Count);
        Assert.Equal("动作5", Prop<string>(p1.List[0], "action"));
        Assert.Single(p2.List);
        Assert.Equal("动作1", Prop<string>(p2.List[0], "action"));
    }

    [Fact]
    public async Task QueryAsync_PageSizeClampedAndPageFloor()
    {
        for (var i = 1; i <= 3; i++)
            SeedLog("销售管理", $"动作{i}", null, DateTime.Today.AddHours(i));

        var page = await QueryAsync(null, null, null, 0, 0);   // 页码/页长非法

        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(3, page.List.Count);
    }

    [Fact]
    public async Task QueryAsync_PageSizeOverLimit_CappedAt200()
    {
        var page = await QueryAsync(null, null, null, 1, 5000);

        Assert.Equal(200, page.PageSize);
    }

    [Fact]
    public async Task QueryAsync_ProjectsFormattedTimeAndFields()
    {
        var at = new DateTime(2026, 5, 6, 7, 8, 9);
        SeedLog("库存管理", "盘点", "货架A", at);

        var page = await QueryAsync();
        var row = page.List[0];

        Assert.Equal("2026-05-06 07:08:09", Prop<string>(row, "createdAt"));
        Assert.Equal("库存管理", Prop<string>(row, "module"));
        Assert.Equal("盘点", Prop<string>(row, "action"));
        Assert.Equal("货架A", Prop<string>(row, "target"));
        Assert.Equal("127.0.0.1", Prop<string>(row, "ip"));
        Assert.True(Prop<long>(row, "id") > 0);                 // 日志主键为 bigint
    }

    [Fact]
    public async Task QueryAsync_NullTarget_DoesNotThrowOnKeyword()
    {
        SeedLog("库存管理", "盘点", null, DateTime.Today);

        var page = await QueryAsync("盘点");

        Assert.Single(page.List);
        Assert.Null(Prop<string>(page.List[0], "target"));
    }
}
