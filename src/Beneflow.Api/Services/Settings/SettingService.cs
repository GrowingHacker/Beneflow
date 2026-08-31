using System.Text.Json;
using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>系统设置：按组 JSON 落库；barcode 组的 API Key 加密存储 + 前端脱敏回显</summary>
public class SettingService : ISettingService
{
    private const int BackupRetentionDays = 30;
    private readonly AppDbContext _db;
    private readonly AesStringCipher _cipher;
    private readonly ILogService _logs;
    private readonly string _connString;
    private readonly string _backupDir;
    public SettingService(AppDbContext db, AesStringCipher cipher, ILogService logs, IConfiguration cfg, IWebHostEnvironment env)
    {
        _db = db; _cipher = cipher; _logs = logs;
        _connString = cfg.GetConnectionString("Default") ?? "";
        // 备份目录：appsettings Backup:Dir 优先（本机配置 d:\Beneflow\backups），缺省落在项目目录 backups/ 下
        _backupDir = cfg["Backup:Dir"] ?? Path.Combine(env.ContentRootPath, "backups");
    }

    private string DbName => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connString).InitialCatalog;
    /// <summary>备份文件目录（自动/手动共用，保留 30 天）</summary>
    public string BackupDirPath => _backupDir;

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

    /// <summary>全量备份到本地目录（保留 30 天，过期自动清理），返回 .bak 文件完整路径</summary>
    public async Task<string> BackupToFileAsync()
    {
        var dir = _backupDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DbName}_{DateTime.Now:yyyyMMddHHmmss}.bak");
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_connString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"BACKUP DATABASE {SafeName(DbName)} TO DISK = N'{file}' WITH INIT, NAME = N'Beneflow 全量备份';");
        CleanOldBackups(dir);
        return file;
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

        Directory.CreateDirectory(_backupDir);
        var file = Path.Combine(_backupDir, $"restore_upload_{DateTime.Now:yyyyMMddHHmmss}.bak");
        await using (var fs = System.IO.File.Create(file))
            await bakStream.CopyToAsync(fs);

        var db = SafeName(DbName);
        var masterCsb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connString) { InitialCatalog = "master" };
        ApiResult result;
        try
        {
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(masterCsb.ConnectionString);
            await conn.OpenAsync();
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
