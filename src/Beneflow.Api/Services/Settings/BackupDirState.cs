namespace Beneflow.Api.Services;

/// <summary>
/// 备份目录的**进程级**状态：首选目录写不进去时退到哪儿、给 SQL Server 服务账号的授权试过哪些目录。
///
/// 为什么要单独做成单例：<see cref="SettingService"/> 注册为 Scoped，而宿主备份服务每次备份都开新 scope；
/// 而这两件事都是**跟请求无关的客观事实**（本机 SQL 服务账号就是写不进那个目录 / ACL 已经改过了），
/// 必须跨 scope 共享 —— 否则每次备份都要先在首选目录白失败一遍，日志里天天一条假告警。
///
/// 并发说明：两个备份同时跑时这里可能各写各的，但最坏结果只是多重试一次，不影响正确性。
/// </summary>
public class BackupDirState
{
    /// <summary>已经确认写得进去、以后就用它的回退目录；null = 仍用首选目录</summary>
    public string? FallbackDir { get; set; }

    /// <summary>最近一次已经尝试过（或已确认不需要）授权给 SQL Server 服务账号的目录。
    /// 授权是改磁盘上的 ACL，同一个目录做一次就够；换目录（走了回退）才会再试一次。</summary>
    public string? GrantAttemptedDir { get; set; }
}
