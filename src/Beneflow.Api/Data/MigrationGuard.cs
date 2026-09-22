using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Data;

/// <summary>
/// 启动自检：库结构有没有落后于代码（即「加了迁移类但没应用」）。
/// </summary>
/// <remarks>
/// 背景：EF 迁移是「代码先行」的 —— 实体 / 配置改完只 add 一个迁移类，**数据库是不会自己跟着变的**，
/// 必须再执行一次 database update（发行包走 <c>Beneflow.Api.exe --migrate</c>）。
/// 漏了这一步的运行态很阴：进程照常启动、绝大多数接口照常工作，只有那些查询了新列的接口会 500
/// （SQL Server 报 <c>Invalid column name 'Xxx'</c>），现象离原因很远，得顺着栈翻到 SQL 才看得出来。
/// 所以在启动时主动查一次待应用迁移，把它变成「启动日志里一条点名道姓的错误」。
///
/// 本类只负责「查」和「怎么说」，不负责「怎么办」——记日志还是拒绝启动是策略，留在 Program.cs 里。
/// </remarks>
public static class MigrationGuard
{
    /// <summary>自检结论</summary>
    public enum MigrationState
    {
        /// <summary>迁移已全部应用，库结构与代码一致。</summary>
        UpToDate,

        /// <summary>存在未应用的迁移 —— 这个进程正在带病运行。</summary>
        Pending,

        /// <summary>查不出来（数据库不可达、或提供程序不支持迁移）。**不代表没问题**。</summary>
        Unavailable,
    }

    /// <param name="State">结论</param>
    /// <param name="Pending">未应用的迁移名（按应用顺序），仅 <see cref="MigrationState.Pending"/> 时非空</param>
    /// <param name="Error">查询失败原因，仅 <see cref="MigrationState.Unavailable"/> 时非空</param>
    public readonly record struct CheckResult(
        MigrationState State, IReadOnlyList<string> Pending, string? Error);

    /// <summary>
    /// 查询待应用的迁移。**本方法不抛异常**：任何失败都折叠成 <see cref="MigrationState.Unavailable"/>，
    /// 由调用方决定怎么记日志 —— 一道自检不该有能力把进程搞挂。
    /// </summary>
    public static async Task<CheckResult> InspectAsync(AppDbContext db)
    {
        try
        {
            return Classify((await db.Database.GetPendingMigrationsAsync()).ToList());
        }
        catch (Exception ex)
        {
            // 走到这里的两种情况：
            //   1. 非关系型提供程序（单测用的 InMemory）—— 根本没有迁移概念；
            //   2. 连接串坏到连 `__EFMigrationsHistory` 都查不了。
            // 两者都说明「这次自检没结论」，而不是「没问题」。
            return new CheckResult(MigrationState.Unavailable, Array.Empty<string>(), ex.Message);
        }
    }

    /// <summary>
    /// 把「待应用迁移列表」判定成结论。
    /// </summary>
    /// <remarks>
    /// 单独抽成一个纯函数是为了**可测**：列表怎么来的依赖数据库提供程序 ——
    /// InMemory 没有迁移概念、只会抛异常，也就是说「空列表 = 一致」这条判定在单测里永远走不到。
    /// 而它恰恰是本类的核心（判错方向就是每次启动刷一条假告警），所以必须能直接断言。
    /// </remarks>
    public static CheckResult Classify(IReadOnlyList<string> pending) =>
        pending.Count == 0
            ? new CheckResult(MigrationState.UpToDate, pending, null)
            : new CheckResult(MigrationState.Pending, pending, null);

    /// <summary>
    /// 待应用迁移的告警文案：一次说清「会出什么错」（500 / Invalid column name）
    /// 和「跑哪条命令」，免得看到日志的人还得回来翻 README。
    /// </summary>
    public static string BuildPendingMessage(IReadOnlyList<string> pending) =>
        $"库结构落后于代码：检测到 {pending.Count} 个未应用的数据库迁移。"
        + "这些迁移新增的列/表在库里尚不存在，涉及它们的接口会返回 500（SQL Server 报 Invalid column name）。"
        + "请先执行 dotnet ef database update --configuration Release；"
        + "发行包部署则执行 .\\Beneflow.Api.exe --migrate。"
        + $"待应用迁移：{string.Join("、", pending)}";
}
