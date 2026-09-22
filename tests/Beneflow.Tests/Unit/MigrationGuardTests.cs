using Beneflow.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 启动自检（<see cref="MigrationGuard"/>）的行为契约。
///
/// 这道自检的用途是「把『加了迁移没应用』从页面 500 提前到启动日志」，它自己不查出问题时不能变成新故障源，
/// 所以要守住三条：
/// ① 绝不抛异常 —— 任何失败都折叠成 Unavailable，是否致命由调用方决定；
/// ② 绝不误报 —— 「空列表 = 库结构与代码一致」这条判定判反了，就是每次启动刷一条假告警，
///    真出问题时反而没人信；
/// ③ 文案要能自救 —— 同时给出「会报什么错」和「跑哪条命令」，并点名每个迁移。
///
/// 注意 ② 的测法：判定逻辑走 <see cref="MigrationGuard.Classify"/> 的用例，不要指望 InMemory 那条 ——
/// 单测用的 InMemory 没有迁移概念、GetPendingMigrationsAsync 直接抛异常，压根走不到判定那一行
/// （这一点是被一次假绿的变异验证揪出来的：把判定方向反过来，InMemory 用例照样全绿）。
/// </summary>
public class MigrationGuardTests : TestBase
{
    /// <summary>
    /// 单测环境用 InMemory，它没有迁移概念：InspectAsync 只能要么抛、要么给空集。
    /// 这里守住的是「异常不许逃出自检」+「不许在无迁移概念的提供程序上喊库结构落后」；
    /// 判定方向本身由下面的 Classify 用例负责。
    /// </summary>
    [Fact]
    public async Task InspectAsync_InMemory_异常不逃出自检()
    {
        var result = await MigrationGuard.InspectAsync(Db);

        Assert.NotEqual(MigrationGuard.MigrationState.Pending, result.State);
        Assert.Empty(result.Pending);
        // 自检失败时必须带回原因，否则调用方只能记一条「出错了，不知道为啥」的日志
        if (result.State == MigrationGuard.MigrationState.Unavailable)
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    /// <summary>
    /// 库连不上（连接串指向一个不存在的 LocalDB 实例）时，自检要把异常折叠成 Unavailable 并带回原因，
    /// 而不是把异常抛给启动流程 —— 连通性问题由随后的种子数据初始化报出更具体的原因。
    /// </summary>
    [Fact]
    public async Task InspectAsync_数据库不可达_折叠成不可用且带回原因()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            // 故意指向不存在的 LocalDB 实例：本地解析失败、不会去连真实服务器，
            // 因此这条用例既不依赖机器上装了 SQL Server，也不会等网络超时。
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB_NoSuchInstance;Database=BeneflowGuardTest;"
                          + "Trusted_Connection=True;Connect Timeout=1")
            .Options;
        using var db = new AppDbContext(options);

        var result = await MigrationGuard.InspectAsync(db);

        Assert.Equal(MigrationGuard.MigrationState.Unavailable, result.State);
        Assert.Empty(result.Pending);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    /// <summary>没有待应用迁移 = 库结构与代码一致。判成 Pending 就是假告警，每次启动都刷。</summary>
    [Fact]
    public void Classify_空列表_判为一致()
    {
        var result = MigrationGuard.Classify(Array.Empty<string>());

        Assert.Equal(MigrationGuard.MigrationState.UpToDate, result.State);
        Assert.Empty(result.Pending);
        Assert.Null(result.Error);
    }

    /// <summary>有待应用迁移 = 库结构落后；列表要原样带出来（日志里要能点名是哪个迁移没应用）。</summary>
    [Fact]
    public void Classify_有迁移_判为落后并原样带回列表()
    {
        var pending = new List<string> { "20260922064344_AddPurchaseReturnVoided", "20261001090000_AddFooBar" };

        var result = MigrationGuard.Classify(pending);

        Assert.Equal(MigrationGuard.MigrationState.Pending, result.State);
        Assert.Equal(pending, result.Pending);
        Assert.Null(result.Error);
    }

    /// <summary>文案：计数、每个迁移名、后果（500）、两条修复命令，缺一样看到日志的人就得回来翻文档。</summary>
    [Fact]
    public void BuildPendingMessage_说清后果与两条修复命令并点名每个迁移()
    {
        var pending = new List<string> { "20260922064344_AddPurchaseReturnVoided", "20261001090000_AddFooBar" };

        var msg = MigrationGuard.BuildPendingMessage(pending);

        Assert.Contains("检测到 2 个未应用的数据库迁移", msg);
        Assert.Contains("20260922064344_AddPurchaseReturnVoided", msg);
        Assert.Contains("20261001090000_AddFooBar", msg);
        Assert.Contains("dotnet ef database update --configuration Release", msg);
        Assert.Contains("Beneflow.Api.exe --migrate", msg);
        Assert.Contains("500", msg);
    }
}
