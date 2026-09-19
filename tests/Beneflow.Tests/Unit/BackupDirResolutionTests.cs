using Beneflow.Api.Services;

namespace Beneflow.Tests.Unit;

/// <summary>
/// 备份目录解析的回归测试（不继承 <see cref="TestBase"/>：被测的是纯静态函数，不需要库）。
///
/// 守的是一个真实踩过的坑（2026-09-20 全新部署沙箱实测）：
/// `Backup:Dir` 配成相对路径 `backups` 时，app 把相对路径**原样**交给 SQL Server，
/// 而 SQL Server 会把它锚到**自己**的默认备份目录（`...\MSSQL\Backup\backups`）；
/// app 那边却按**进程当前目录**建了另一个 `backups` —— 两个文件夹不是同一个，
/// 于是「初始化数据」的自动备份报 *操作系统错误 3(系统找不到指定的路径)*。
/// 本地开发没暴露，是因为开发用的 appsettings.json 写的是绝对路径。
///
/// 断言核心只有一条：**交到 SQL Server 手里的路径必须是绝对路径**。
/// </summary>
public class BackupDirResolutionTests
{
    /// <summary>假装 app 装在 D:\apps\beneflow</summary>
    private const string Root = @"D:\apps\beneflow";

    [Fact]
    public void ResolveBackupDir_NotConfigured_FallsBackToAppFolder()
    {
        var dir = SettingService.ResolveBackupDir(null, Root);

        Assert.Equal(Path.Combine(Root, "backups"), dir);
        Assert.True(Path.IsPathRooted(dir));
    }

    /// <summary>
    /// 配置写成 `"Dir": ""`（模板里很常见的占位写法）时拿到的是**空串而不是 null**，
    /// 用 `?? ` 兜不住、会原样透传成相对路径，又踩回同一个坑。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBackupDir_Blank_TreatedAsNotConfigured(string configured)
    {
        var dir = SettingService.ResolveBackupDir(configured, Root);

        Assert.Equal(Path.Combine(Root, "backups"), dir);
        Assert.True(Path.IsPathRooted(dir));
    }

    [Theory]
    [InlineData("backups")]
    [InlineData(@"data\bak")]
    [InlineData(@".\backups")]
    public void ResolveBackupDir_RelativePath_AnchoredToContentRoot(string configured)
    {
        var dir = SettingService.ResolveBackupDir(configured, Root);

        Assert.True(Path.IsPathRooted(dir), $"相对路径必须被转成绝对路径，实际拿到：{dir}");
        Assert.StartsWith(Root, dir);
    }

    [Fact]
    public void ResolveBackupDir_AbsolutePath_KeptAsIs()
    {
        var dir = SettingService.ResolveBackupDir(@"D:\Beneflow\backups", Root);

        Assert.Equal(@"D:\Beneflow\backups", dir);
    }

    /// <summary>总纲：不管怎么配，出去的一律是绝对路径 —— 这正是当初那个 bug 的判据。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("backups")]
    [InlineData(@"data\bak")]
    [InlineData(@"D:\Beneflow\backups")]
    [InlineData(@"C:\Program Files\Beneflow\backups")]
    public void ResolveBackupDir_AlwaysReturnsRootedPath(string? configured)
    {
        Assert.True(Path.IsPathRooted(SettingService.ResolveBackupDir(configured, Root)));
    }
}
