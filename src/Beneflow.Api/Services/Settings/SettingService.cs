using System.Text.Json;
using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>系统设置：按组 JSON 落库；barcode 组的 API Key 加密存储 + 前端脱敏回显</summary>
public partial class SettingService : ISettingService
{
    private const int BackupRetentionDays = 30;
    private readonly AppDbContext _db;
    private readonly AesStringCipher _cipher;
    private readonly ILogService _logs;
    private readonly ILogger<SettingService>? _log;
    private readonly BackupDirState _backupState;
    private readonly string _connString;
    /// <summary>配置解析出的首选备份目录（一定是绝对路径，见 <see cref="ResolveBackupDir"/>）</summary>
    private readonly string _backupDir;

    public SettingService(AppDbContext db, AesStringCipher cipher, ILogService logs, IConfiguration cfg, IWebHostEnvironment env,
        BackupDirState backupState, ILogger<SettingService>? log = null)
    {
        _db = db; _cipher = cipher; _logs = logs; _backupState = backupState; _log = log;
        _connString = cfg.GetConnectionString("Default") ?? "";
        // 备份目录：appsettings Backup:Dir 优先（本机配置绝对路径 d:\Beneflow\backups），缺省落在 app 目录 backups/ 下
        _backupDir = ResolveBackupDir(cfg["Backup:Dir"], env.ContentRootPath);
    }

    /// <summary>
    /// 解析备份目录，**保证交到 SQL Server 手里的一定是绝对路径**。
    ///
    /// 为什么必须绝对：`BACKUP ... TO DISK = N'相对路径'` 里的相对路径**不是**按进程当前目录解析的，
    /// 而是被 SQL Server 锚到它自己的默认备份目录（`SERVERPROPERTY('InstanceDefaultBackupPath')`，
    /// 通常是 `C:\Program Files\Microsoft SQL Server\&lt;实例&gt;\MSSQL\Backup`）。而 app 这一侧的
    /// `Directory.CreateDirectory` 是按**进程当前目录**建的 —— 两边指向不同文件夹，于是「目录明明建了、
    /// 却报操作系统错误 3(系统找不到指定的路径)」（2026-09-20 全新部署时实测踩到）。
    /// 所以：相对路径一律锚到 ContentRootPath（app 自己所在目录）后再交给 SQL Server；空/空白视同未配置。
    /// </summary>
    internal static string ResolveBackupDir(string? configured, string contentRoot)
    {
        // 注意不能用 `?? `：配置写成 "Dir": "" 时拿到的是空串而非 null，会原样透传成相对路径、又踩回同一个坑
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(contentRoot, "backups");
        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
    }

    private string DbName => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connString).InitialCatalog;

    /// <summary>连到 master 的连接串（查 SERVERPROPERTY / sys.dm_server_services、ALTER DATABASE 都要它）</summary>
    private string MasterConnString =>
        new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connString) { InitialCatalog = "master" }.ConnectionString;

    /// <summary>备份文件目录（自动/手动共用，保留 30 天）。没回退过=首选目录；回退过=回退目录。</summary>
    public string BackupDirPath => _backupState.FallbackDir ?? _backupDir;

    public async Task<object> GetAsync()
    {
        // 默认值与种子数据一致；库里存的就是前端提交的原样 JSON，按组直接回放
        var defaults = new Dictionary<string, object?>
        {
            ["shop"] = new { name = "百惠通便利店", phone = "020-88888888", address = "中山市东区XX路1号", logo = "" },
            ["sale"] = new { allowCredit = true, defaultPayMethod = "现金" },
            ["receipt"] = new { header = "百惠通便利店", footer = "欢迎再次光临", autoPrint = false },
            ["stock"] = new { warningThreshold = 1, expiryDays = 30 },
            ["units"] = new[] { "个", "瓶", "盒", "箱", "袋", "包", "罐", "听", "支", "条", "桶", "杯", "份", "块", "双", "张", "斤", "千克", "克", "升", "毫升" },
            ["specs"] = new[] { "常规", "散装", "500ml", "330ml", "550ml", "250ml", "1L", "1.5L", "500g", "250g", "100g", "1kg", "10kg", "20支" },
            // 分类专属字典：键=分类名，值为该分类可选的单位/规格；未配置的分类使用全局 units/specs
            ["categoryDict"] = new Dictionary<string, object>(),
            ["payMethods"] = new[] { "现金", "微信", "支付宝", "赊账" },
            // 条码联网查询的 API Key：留空走匿名。ApiZero 留空=匿名(20次/天)；填 Key=200次/天
            ["barcode"] = new { apiZeroKey = "" },
        };

        var rows = await _db.SystemConfigs.AsNoTracking().ToListAsync();
        foreach (var row in rows)
        {
            if (!defaults.ContainsKey(row.ConfigKey)) continue;
            try
            {
                using var doc = JsonDocument.Parse(row.ConfigValue);
                var cloned = doc.RootElement.Clone();   // doc 释放后仍可用
                // barcode 组含敏感 API Key：库内存密文，返回给前端前先解密再脱敏，避免明文外泄
                if (row.ConfigKey == "barcode" && cloned.ValueKind == JsonValueKind.Object)
                {
                    var encKey = cloned.TryGetProperty("apiZeroKey", out var ak) && ak.ValueKind == JsonValueKind.String ? ak.GetString() : "";
                    var plainKey = _cipher.Decrypt(encKey) ?? "";
                    defaults[row.ConfigKey] = new { apiZeroKey = AesStringCipher.Mask(plainKey) };
                }
                else
                {
                    defaults[row.ConfigKey] = cloned;
                }
            }
            catch { /* 坏值回退默认 */ }
        }
        return defaults;
    }

    /// <summary>整体保存：body 为 Settings 页提交的嵌套对象，逐组 JSON 落库。
    /// barcode 组的 apiZeroKey 特殊处理：前端回显的是脱敏值（***后4位），
    /// 若提交值仍为脱敏占位符则视为"未修改"，保留库内原密文；否则加密后落库。</summary>
    public async Task<ApiResult> SaveAsync(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return ApiResult.Fail("参数格式错误");
        foreach (var prop in body.EnumerateObject())
        {
            var cfg = await _db.SystemConfigs.FirstOrDefaultAsync(c => c.ConfigKey == prop.Name);

            // barcode 组：对 apiZeroKey 做加密/保留原值处理
            string value;
            if (prop.Name == "barcode" && prop.Value.ValueKind == JsonValueKind.Object)
            {
                value = await SaveBarcodeSettingsAsync(prop.Value, cfg);
            }
            else
            {
                value = prop.Value.ToString(); // Value 的原始字符串
            }

            if (cfg == null)
                _db.SystemConfigs.Add(new SystemConfig { ConfigKey = prop.Name, ConfigValue = value });
            else
            {
                cfg.ConfigValue = value;
                cfg.UpdatedAt = DateTime.Now;
            }
        }
        await _logs.WriteAsync("系统管理", "保存系统设置", null);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    /// <summary>处理 barcode 组保存：apiZeroKey 脱敏占位符→保留原密文；否则加密落库。</summary>
    private async Task<string> SaveBarcodeSettingsAsync(JsonElement barcodeVal, SystemConfig? existing)
    {
        // 取前端提交的 apiZeroKey
        var submittedKey = barcodeVal.TryGetProperty("apiZeroKey", out var ak) && ak.ValueKind == JsonValueKind.String
            ? ak.GetString() ?? "" : "";

        string encKey;
        if (AesStringCipher.IsMasked(submittedKey) && existing != null)
        {
            // 脱敏占位符 = 未修改，保留库内原密文
            try
            {
                using var ex = JsonDocument.Parse(existing.ConfigValue);
                encKey = ex.RootElement.TryGetProperty("apiZeroKey", out var ek) && ek.ValueKind == JsonValueKind.String
                    ? ek.GetString() ?? "" : "";
            }
            catch { encKey = ""; }
        }
        else
        {
            // 新值（含清空）：加密后落库。空值加密后仍是空（Encrypt 对空返回空）
            encKey = _cipher.Encrypt(submittedKey) ?? "";
        }

        // 重组 barcode JSON（目前只有 apiZeroKey 一个字段）
        return $$"""{"apiZeroKey":{{JsonSerializer.Serialize(encKey)}}}""";
    }

    // ================= 备份与恢复（SQL Server 全量 .bak） =================

    private static string SafeName(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>
    /// 全量备份（保留 30 天，过期自动清理），返回 .bak 文件完整路径。
    ///
    /// **落点为什么可能不是配置的那个目录**：写备份文件的是 SQL Server 的**服务账号**
    /// （如 <c>NT Service\MSSQL$SQLEXPRESS</c>），不是 app 进程 —— 两边权限是两回事。
    /// app 装在用户 profile（<c>C:\Users\...</c>）或 Program Files 下时，服务账号对那里没有写权限，
    /// <c>BACKUP ... TO DISK</c> 就报「操作系统错误 5(拒绝访问)」（2026-09-20 沙箱全新部署实测）。
    /// 所以：先在配置的目录上试（试之前尽力把目录授权给 SQL 服务账号），不行就按 <see cref="FallbackDirsAsync"/>
    /// 的顺序退，并把真正用上的目录记在进程里（后续备份直接用，不再每次先白失败一遍）。
    ///
    /// 结论：只可能「成功」或「连备选目录都写不进去」，不会再出现「路径是对的、就是写不进去」这种失败。
    /// </summary>
    public Task<string> BackupToFileAsync() => BackupWithFallbackAsync(
        BackupDirPath,
        BackupToDirAsync,
        FallbackDirsAsync,
        dir =>
        {
            _backupState.FallbackDir = dir;
            _log?.LogWarning("备份目录不可写，本次起回退到：{Dir}", dir);
        });

    /// <summary>
    /// 首选目录写不进去时的回退候选（按优先级）：
    /// 1. <c>%ProgramData%\Beneflow\backups</c> —— **两边都能用**的地方：app 建目录、app 读写，
    ///    而它继承来的 ACL（Users 可创建、CREATOR OWNER 拿全权）又让 SQL 服务账号能写进去，实测无需额外授权；
    /// 2. SQL Server 自己的默认备份目录（<c>InstanceDefaultBackupPath</c>）—— 最后兜底：
    ///    服务账号在那里**一定**写得进去，代价是 app 可能读不到（手动下载会失败，已在控制器里容错成一句提示）。
    /// </summary>
    private async Task<IReadOnlyList<string?>> FallbackDirsAsync()
        => new[] { ProgramDataBackupDir(), await QuerySqlDefaultBackupDirAsync() };

    /// <summary>机器级公共目录下的备份目录（Windows 是 %ProgramData%；其它平台取系统等价目录）</summary>
    internal static string ProgramDataBackupDir()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Path.GetTempPath(), "Beneflow", "backups")   // 理论上到不了这
            : Path.Combine(root, "Beneflow", "backups");
    }

    /// <summary>往指定目录做一次全量备份：准备目录 → BACKUP → 清理过期</summary>
    internal async Task<string> BackupToDirAsync(string dir)
    {
        await PrepareDirForSqlAsync(dir);
        var file = Path.Combine(dir, $"{DbName}_{DateTime.Now:yyyyMMddHHmmss}.bak");
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"BACKUP DATABASE {SafeName(DbName)} TO DISK = N'{file}' WITH INIT, NAME = N'Beneflow 全量备份';");
        // 清理是附带动作：万一退到了 SQL 自己的 Backup 目录（app 可能不允许枚举），读不到就跳过，
        // 别把「备份已经成功」搞成「备份失败」
        try { CleanOldBackups(dir); }
        catch (Exception ex) { _log?.LogWarning(ex, "清理过期备份失败（不影响本次备份）：{Dir}", dir); }
        return file;
    }

    /// <summary>
    /// 「先试原本的目录，不行就按回退候选的顺序依次退」的流程骨架。
    /// 抽成静态纯函数是为了让这条路径**能被单测守住**：真实 SQL 服务账号的权限故障没法在单测里复现，
    /// 但「什么时候该退、退到第几个、全失败怎么报」可以 —— attempt / query 都由调用方注入。
    /// 保证：每个目录最多试一次（不会来回弹）；只有真正用上了回退目录才回调 <paramref name="onFallback"/>
    /// （调用方据此记住新落点）；没有回退可用时**原样抛出**（连堆栈一起保留）；
    /// 多个目录全失败时抛的异常**带上每个目录的原因**，便于排查。
    /// </summary>
    internal static async Task<string> BackupWithFallbackAsync(
        string dir,
        Func<string, Task<string>> attempt,
        Func<Task<IReadOnlyList<string?>>> queryFallbackDirs,
        Action<string> onFallback)
    {
        try
        {
            return await attempt(dir);
        }
        catch (Exception primaryEx)
        {
            var fallbacks = await BuildFallbacksAsync(dir, queryFallbackDirs);
            if (fallbacks.Count == 0) throw;

            var reasons = new List<string> { $"{dir}：{primaryEx.Message}" };
            foreach (var fallback in fallbacks)
            {
                try
                {
                    var file = await attempt(fallback);
                    onFallback(fallback);
                    return file;
                }
                catch (Exception ex)
                {
                    reasons.Add($"{fallback}：{ex.Message}");
                }
            }

            throw new InvalidOperationException(
                $"备份失败（已依次尝试 {reasons.Count} 个目录）：{string.Join("；", reasons)}", primaryEx);
        }
    }

    /// <summary>回退候选去重：与首选目录相同、空值、彼此重复的都剔掉（同一个目录试两遍没意义）。</summary>
    private static async Task<List<string>> BuildFallbacksAsync(
        string primaryDir, Func<Task<IReadOnlyList<string?>>> queryFallbackDirs)
    {
        IReadOnlyList<string?> raw;
        try { raw = await queryFallbackDirs(); }
        catch { raw = Array.Empty<string?>(); }   // 问不到（非关系库 / 没权限）→ 就当没有回退可选

        var dirs = new List<string>();
        foreach (var d in raw)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            if (SameDir(d!, primaryDir) || dirs.Any(x => SameDir(x, d!))) continue;
            dirs.Add(d!);
        }
        return dirs;
    }

    /// <summary>两个路径是否同一个目录：忽略末尾分隔符与大小写（Windows 路径不区分大小写）。</summary>
    internal static bool SameDir(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }   // 路径非法：当作不同，让调用方去试回退目录更安全
    }

    /// <summary>
    /// 备份/恢复前的目录准备：建目录 + 尽力让 SQL Server 服务账号能写进去。
    /// 授权只在每个目录上做一次（ACE 落在磁盘上，重复做没意义）；授权失败只记日志、不抛，由回退兜底。
    /// </summary>
    private async Task PrepareDirForSqlAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        if (_backupState.GrantAttemptedDir is { } done && SameDir(done, dir)) return;
        _backupState.GrantAttemptedDir = dir;
        try { await GrantSqlServiceWriteAsync(dir); }
        catch (Exception ex) { _log?.LogWarning(ex, "给 SQL Server 服务账号授权备份目录失败（由回退兜底）：{Dir}", dir); }
    }

    /// <summary>
    /// 尽力把目录授权给 SQL Server 的服务账号（Modify + 子项继承）。
    /// 只有「本机 / 问得到账号名 / 当前进程对该目录有改权限（属主或管理员）」时才做得到，做不到就静默放手。
    /// </summary>
    private async Task GrantSqlServiceWriteAsync(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var account = await QuerySqlServiceAccountAsync();
        if (string.IsNullOrWhiteSpace(account)) return;

        var sid = new System.Security.Principal.NTAccount(account)
            .Translate(typeof(System.Security.Principal.SecurityIdentifier));
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            sid, System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
        _log?.LogInformation("已授权 SQL Server 服务账号写入备份目录：{Account} → {Dir}", account, dir);
    }

    /// <summary>SQL Server 自己的默认备份目录：服务账号在那里一定写得进去，是回退的落脚点。</summary>
    internal async Task<string?> QuerySqlDefaultBackupDirAsync()
    {
        if (string.IsNullOrWhiteSpace(_connString)) return null;
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(MasterConnString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'))";
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>SQL Server 引擎服务跑在哪个账号下（sys.dm_server_services 需要 VIEW SERVER STATE，问不到返回 null）。</summary>
    private async Task<string?> QuerySqlServiceAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(_connString)) return null;
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(MasterConnString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'";
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static void CleanOldBackups(string dir)
    {
        var threshold = DateTime.Now.AddDays(-BackupRetentionDays);
        foreach (var f in Directory.GetFiles(dir, "*.bak"))
            if (System.IO.File.GetLastWriteTime(f) < threshold) { try { System.IO.File.Delete(f); } catch { /* 占用即跳过 */ } }
    }

    /// <summary>从上传的 .bak 恢复数据库：SINGLE_USER 踢连接 → RESTORE WITH REPLACE → 回到 MULTI_USER。</summary>
    public async Task<ApiResult> RestoreAsync(Stream bakStream, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            return ApiResult.Fail("请上传 .bak 备份文件");

        // 落到「实际在用的备份目录」：那里要么已授权给 SQL 服务账号，要么本来就是它自己的默认目录 ——
        // RESTORE 是 SQL Server 去**读**这个文件，与 BACKUP 是同一套权限问题，落点一致才不会又踩一次。
        var dir = BackupDirPath;
        await PrepareDirForSqlAsync(dir);
        var file = Path.Combine(dir, $"restore_upload_{DateTime.Now:yyyyMMddHHmmss}.bak");
        await using (var fs = System.IO.File.Create(file))
            await bakStream.CopyToAsync(fs);

        var db = SafeName(DbName);
        ApiResult result;
        try
        {
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(MasterConnString);
            await conn.OpenAsync();
            // 恢复前必须主动清空连接池：池中被踢掉的失效连接要等下一次通信才被检测到，
            // 首次复用会抛异常（ADO.NET 连接池官方机制）。主动清池让这些连接直接丢弃、不再归还。
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            await ExecAsync(conn, $"ALTER DATABASE {db} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
            try
            {
                await ExecAsync(conn, $"RESTORE DATABASE {db} FROM DISK = N'{file}' WITH REPLACE, RECOVERY;");
                result = ApiResult.Ok();
            }
            catch (Exception ex)
            {
                // 恢复失败：尽量把库从 RESTORING 状态拉回在线，避免服务不可用
                try { await ExecAsync(conn, $"RESTORE DATABASE {db} WITH RECOVERY;"); } catch { }
                result = ApiResult.Fail("恢复失败：" + ex.Message);
            }
            finally
            {
                try { await ExecAsync(conn, $"ALTER DATABASE {db} SET MULTI_USER;"); } catch { }
            }
        }
        catch (Exception ex)
        {
            result = ApiResult.Fail("恢复失败：" + ex.Message);
        }
        try { System.IO.File.Delete(file); } catch { }
        return result;
    }

    private static async Task ExecAsync(Microsoft.Data.SqlClient.SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }
}
