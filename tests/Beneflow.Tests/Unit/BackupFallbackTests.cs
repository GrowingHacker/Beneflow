using Beneflow.Api.Services;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 备份「写不进去就换目录」这条兜底路径的回归测试（不继承 <see cref="TestBase"/>：被测的是纯逻辑，不碰库、不碰真 SQL）。
///
/// 守的是真实踩过的第二个坑（2026-09-20 沙箱全新部署实测）：
/// 备份文件是 **SQL Server 服务账号**（如 NT Service\MSSQL$SQLEXPRESS）写的，不是 app 进程写的 ——
/// app 装在用户 profile（C:\Users\...）或 Program Files 下时，服务账号对那里没有写权限，
/// 于是「初始化数据」清空前的自动备份报 **操作系统错误 5(拒绝访问)**。
/// 修法三道：
///   1. 先试配置的目录，试之前尽力把它授权给 SQL 服务账号（同机、拿得到账号名时）；
///   2. 还不行 → %ProgramData%\Beneflow\backups（app 与 SQL 服务账号**都**能读写）；
///   3. 再不行 → SQL 自己的默认备份目录（服务账号一定写得进去，代价是 app 可能读不到）。
/// 这个文件测的是 1~3 的调度逻辑，也正是「保证用户部署后不会再撞上」的那部分：
/// **该退时必须退、每个目录只试一次、只有真用上了才记住它、全失败时把每个目录的原因都带上**。
/// </summary>
public class BackupFallbackTests
{
    /// <summary>app 自己的备份目录（首选）</summary>
    private const string AppDir = @"C:\apps\beneflow\backups";
    /// <summary>中间那层回退：机器级公共目录（app 与 SQL 服务账号都能读写）</summary>
    private const string ProgramDataDir = @"C:\ProgramData\Beneflow\backups";
    /// <summary>最后兜底：SQL Server 实例的默认备份目录</summary>
    private const string SqlDir = @"C:\Program Files\Microsoft SQL Server\MSSQL17.SQLEXPRESS\MSSQL\Backup";
    private const string Bak = "BeneflowDb_20260920010308.bak";

    /// <summary>假装「往这个目录备份成功了」，返回落点</summary>
    private static Task<string> WriteInto(string dir) => Task.FromResult(Path.Combine(dir, Bak));

    private static Task<string> Fail(string reason) => Task.FromException<string>(new IOException(reason));

    /// <summary>固定的回退候选清单</summary>
    private static Func<Task<IReadOnlyList<string?>>> Fallbacks(params string?[] dirs)
        => () => Task.FromResult<IReadOnlyList<string?>>(dirs);

    [Fact]
    public async Task PrimaryWritable_NoFallbackAndNoQuery()
    {
        var tried = new List<string>();
        var fellBack = new List<string>();
        var queried = 0;

        var file = await SettingService.BackupWithFallbackAsync(
            AppDir,
            dir => { tried.Add(dir); return WriteInto(dir); },
            () => { queried++; return Task.FromResult<IReadOnlyList<string?>>(new[] { SqlDir }); },
            fellBack.Add);

        Assert.Equal(Path.Combine(AppDir, Bak), file);
        Assert.Equal(new[] { AppDir }, tried);
        Assert.Equal(0, queried);     // 首选就成功，连「有哪些回退」都不该去问
        Assert.Empty(fellBack);       // 也不该留下「已回退」的记忆
    }

    /// <summary>沙箱上真实发生的那一幕：首选目录被拒（错误 5）→ 必须自动换到回退目录并成功。</summary>
    [Fact]
    public async Task PrimaryAccessDenied_FallsBackToNextDir()
    {
        var tried = new List<string>();
        var fellBack = new List<string>();

        var file = await SettingService.BackupWithFallbackAsync(
            AppDir,
            dir => { tried.Add(dir); return SettingService.SameDir(dir, AppDir) ? Fail("出现操作系统错误 5(拒绝访问。)") : WriteInto(dir); },
            Fallbacks(ProgramDataDir, SqlDir),
            fellBack.Add);

        Assert.Equal(Path.Combine(ProgramDataDir, Bak), file);
        Assert.Equal(new[] { AppDir, ProgramDataDir }, tried);   // 第一个回退就成，不必再试 SQL 自己的目录
        Assert.Equal(new[] { ProgramDataDir }, fellBack);        // 记住真正用上的那个
    }

    /// <summary>两层回退都失败时，继续往下一个候选走，而不是直接放弃。</summary>
    [Fact]
    public async Task FirstFallbackFails_SecondSucceeds()
    {
        var tried = new List<string>();
        var fellBack = new List<string>();

        var file = await SettingService.BackupWithFallbackAsync(
            AppDir,
            dir => { tried.Add(dir); return SettingService.SameDir(dir, SqlDir) ? WriteInto(dir) : Fail("写不进去"); },
            Fallbacks(ProgramDataDir, SqlDir),
            fellBack.Add);

        Assert.Equal(Path.Combine(SqlDir, Bak), file);
        Assert.Equal(new[] { AppDir, ProgramDataDir, SqlDir }, tried);
        Assert.Equal(new[] { SqlDir }, fellBack);   // 失败的 ProgramData 不该被记成落点
    }

    /// <summary>SQL 报回来的默认备份目录就是首选目录本身（可能只是末尾分隔符不同）→ 别再试一遍，原样报错。</summary>
    [Theory]
    [InlineData(@"C:\apps\beneflow\backups")]
    [InlineData(@"C:\apps\beneflow\backups\")]
    public async Task FallbackSameAsPrimary_NoRetry(string sameAsPrimary)
    {
        var tried = new List<string>();
        var primary = new UnauthorizedAccessException("出现操作系统错误 5(拒绝访问。)");

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SettingService.BackupWithFallbackAsync(
            AppDir,
            dir => { tried.Add(dir); return Task.FromException<string>(primary); },
            Fallbacks(sameAsPrimary),
            _ => { }));

        Assert.Same(primary, ex);              // 原错误原样抛出（`throw;` 保住堆栈）
        Assert.Equal(new[] { AppDir }, tried);
    }

    /// <summary>候选之间的重复也要去掉：同一个目录试两遍没意义。</summary>
    [Fact]
    public async Task DuplicateFallbacks_TriedOnce()
    {
        var tried = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SettingService.BackupWithFallbackAsync(
            AppDir,
            dir => { tried.Add(dir); return Fail("写不进去"); },
            Fallbacks(ProgramDataDir, ProgramDataDir + @"\", ProgramDataDir.ToUpperInvariant()),
            _ => { }));

        Assert.Equal(new[] { AppDir, ProgramDataDir }, tried);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoUsableFallback_RethrowsPrimary(string? fallback)
    {
        var primary = new UnauthorizedAccessException("出现操作系统错误 5(拒绝访问。)");

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SettingService.BackupWithFallbackAsync(
            AppDir,
            _ => Task.FromException<string>(primary),
            Fallbacks(fallback),
            _ => { }));

        Assert.Same(primary, ex);
    }

    /// <summary>问不到回退清单（非关系库 / 没权限问 SQL）时也不能把原始错误吞掉。</summary>
    [Fact]
    public async Task FallbackQueryThrows_RethrowsPrimary()
    {
        var primary = new UnauthorizedAccessException("出现操作系统错误 5(拒绝访问。)");

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SettingService.BackupWithFallbackAsync(
            AppDir,
            _ => Task.FromException<string>(primary),
            () => Task.FromException<IReadOnlyList<string?>>(new InvalidOperationException("SERVERPROPERTY 不可用")),
            _ => { }));

        Assert.Same(primary, ex);
    }

    /// <summary>全部失败：报出来的原因要**每个目录都带上**，否则用户只看到「首选目录被拒」也猜不到后面还试过哪儿。</summary>
    [Fact]
    public async Task AllDirsFail_ErrorCarriesEveryReason()
    {
        var primary = new UnauthorizedAccessException("出现操作系统错误 5(拒绝访问。)");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SettingService.BackupWithFallbackAsync(
            AppDir,
            dir => Task.FromException<string>(SettingService.SameDir(dir, AppDir)
                ? primary
                : new IOException("磁盘空间不足。")),
            Fallbacks(ProgramDataDir, SqlDir),
            _ => { }));

        Assert.Contains("错误 5", ex.Message);
        Assert.Contains("磁盘空间不足", ex.Message);
        Assert.Contains(AppDir, ex.Message);
        Assert.Contains(ProgramDataDir, ex.Message);
        Assert.Contains(SqlDir, ex.Message);
        Assert.Contains("3 个目录", ex.Message);
        Assert.Same(primary, ex.InnerException);
    }

    /// <summary>每个目录最多试一次：不允许「后面的也失败 → 又弹回前面」这种来回。</summary>
    [Fact]
    public async Task AllDirsFail_TriesEachDirAtMostOnce()
    {
        var tried = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SettingService.BackupWithFallbackAsync(
            AppDir,
            dir => { tried.Add(dir); return Fail("写不进去"); },
            Fallbacks(ProgramDataDir, SqlDir),
            _ => { }));

        Assert.Equal(new[] { AppDir, ProgramDataDir, SqlDir }, tried);
    }

    /// <summary>中间层回退是 %ProgramData%\Beneflow\backups：机器级公共目录，app 与 SQL 服务账号都能读写。</summary>
    [Fact]
    public void ProgramDataBackupDir_UnderCommonApplicationData()
    {
        var dir = SettingService.ProgramDataBackupDir();

        Assert.True(Path.IsPathRooted(dir));
        Assert.EndsWith(Path.Combine("Beneflow", "backups"), dir);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(common))
            Assert.StartsWith(common, dir);
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a\b")]
    [InlineData(@"C:\a\b", @"C:\A\B")]        // Windows 路径不区分大小写
    [InlineData(@"C:\a\b\", @"C:\a\b")]       // 末尾分隔符
    [InlineData(@"C:\a\b\\", @"C:\a\b/")]     // 两种分隔符混用
    public void SameDir_Same_ReturnsTrue(string a, string b)
        => Assert.True(SettingService.SameDir(a, b));

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a\bc")]       // 前缀相同但不同目录（不能按字符串前缀判）
    [InlineData(@"C:\a\b", @"D:\a\b")]
    [InlineData("", @"C:\a\b")]               // 空/非法路径：当作不同，宁可多试一次
    public void SameDir_Different_ReturnsFalse(string a, string b)
        => Assert.False(SettingService.SameDir(a, b));
}
