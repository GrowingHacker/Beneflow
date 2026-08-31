namespace Beneflow.Api.Services;

/// <summary>
/// 每日自动备份守护：每天 02:00 全量备份（兑现设置页"每日凌晨 2:00 自动全量备份"承诺）。
/// 考虑到单机部署常在白天开机：启动时若今天 02:00 已错过且当天尚无备份，则立即补备一次。
/// 备份文件保留 30 天（SettingService.BackupToFileAsync 内部统一清理）。
/// </summary>
public class BackupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackupHostedService> _log;
    public BackupHostedService(IServiceScopeFactory scopeFactory, ILogger<BackupHostedService> log)
    { _scopeFactory = scopeFactory; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken st)
    {
        // 启动补备：今天 02:00 已过且目录里没有 02:00 之后生成的备份
        try
        {
            var today2am = DateTime.Today.AddHours(2);
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            if (DateTime.Now >= today2am && !HasBackupAfter(svc.BackupDirPath, today2am))
                await DoBackupAsync("启动补备");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "启动补备失败");
        }

        while (!st.IsCancellationRequested)
        {
            var next = DateTime.Now.Date.AddHours(2);
            if (next <= DateTime.Now) next = next.AddDays(1);
            try { await Task.Delay(next - DateTime.Now, st); }
            catch (TaskCanceledException) { break; }
            try { await DoBackupAsync("每日定时备份"); }
            catch (Exception ex) { _log.LogError(ex, "每日自动备份失败"); }
        }
    }

    private async Task DoBackupAsync(string tag)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var file = await svc.BackupToFileAsync();
        _log.LogInformation("{Tag}完成: {File}", tag, file);
    }

    private static bool HasBackupAfter(string dir, DateTime time)
    {
        if (!Directory.Exists(dir)) return false;
        return Directory.GetFiles(dir, "*.bak").Any(f => System.IO.File.GetLastWriteTime(f) >= time);
    }
}
