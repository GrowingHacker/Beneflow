using System.Text.Json;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>系统设置测试：默认回放、分组落库、API Key 加解密与脱敏、备份目录、非法参数</summary>
public class SettingServiceTests : TestBase
{
    private async Task<JsonElement> AsJson(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>读取配置组的字段值：兼容默认值（匿名对象）与已存配置（JsonElement）</summary>
    private static string? Val(object? group, string field)
    {
        if (group is JsonElement je)
            return je.ValueKind == JsonValueKind.Object && je.TryGetProperty(field, out var v)
                ? v.GetString() : null;
        return Prop<string>(group, field);
    }

    /// <summary>取某个配置组</summary>
    private async Task<object?> Group(string key) =>
        ((Dictionary<string, object?>)await SettingSvc.GetAsync())[key];

    // ==================== 读取 ====================

    [Fact]
    public async Task GetAsync_NoConfig_ReturnsAllDefaultGroups()
    {
        var settings = await SettingSvc.GetAsync();
        var dict = Assert.IsType<Dictionary<string, object?>>(settings);

        Assert.Contains("shop", dict.Keys);
        Assert.Contains("sale", dict.Keys);
        Assert.Contains("receipt", dict.Keys);
        Assert.Contains("stock", dict.Keys);
        Assert.Contains("units", dict.Keys);
        Assert.Contains("specs", dict.Keys);
        Assert.Contains("payMethods", dict.Keys);
        Assert.Contains("barcode", dict.Keys);

        Assert.Equal("百惠通便利店", Val(dict["shop"], "name"));
        Assert.Equal("020-88888888", Val(dict["shop"], "phone"));
    }

    [Fact]
    public async Task GetAsync_PersistedConfig_OverridesDefault()
    {
        SetConfig("shop", """{"name":"测试小店","phone":"123","address":"地址X","logo":""}""");

        var dict = (Dictionary<string, object?>)await SettingSvc.GetAsync();

        Assert.Equal("测试小店", Val(dict["shop"], "name"));
        Assert.Equal("地址X", Val(dict["shop"], "address"));
    }

    [Fact]
    public async Task GetAsync_UnknownKey_Ignored()
    {
        SetConfig("some-unknown-group", """{"foo":1}""");

        var dict = (Dictionary<string, object?>)await SettingSvc.GetAsync();

        Assert.DoesNotContain("some-unknown-group", dict.Keys);
    }

    [Fact]
    public async Task GetAsync_BrokenJson_FallsBackToDefault()
    {
        SetConfig("shop", "{ broken json");

        var dict = (Dictionary<string, object?>)await SettingSvc.GetAsync();

        Assert.Equal("百惠通便利店", Val(dict["shop"], "name"));
    }

    [Fact]
    public async Task GetAsync_BarcodeKey_DecryptedAndMasked()
    {
        var cipher = NewCipher();
        var enc = cipher.Encrypt("abcdefghijklmnop");
        Assert.False(string.IsNullOrEmpty(enc));
        SetConfig("barcode", $$"""{"apiZeroKey":"{{enc}}"}""");

        var dict = (Dictionary<string, object?>)await SettingSvc.GetAsync();
        var masked = Val(dict["barcode"], "apiZeroKey");

        Assert.Equal(AesStringCipher.Mask("abcdefghijklmnop"), masked);
        Assert.True(AesStringCipher.IsMasked(masked));
        Assert.DoesNotContain("abcdefghijklmnop", masked);      // 明文不得外泄
    }

    [Fact]
    public async Task GetAsync_EmptyBarcodeKey_ReturnsEmptyMask()
    {
        SetConfig("barcode", """{"apiZeroKey":""}""");

        var dict = (Dictionary<string, object?>)await SettingSvc.GetAsync();

        Assert.Equal("", Val(dict["barcode"], "apiZeroKey"));
    }

    // ==================== 保存 ====================

    [Fact]
    public async Task SaveAsync_WritesEachGroupAndLogs()
    {
        var body = await AsJson(new
        {
            shop = new { name = "新店名", phone = "020-12345678", address = "广州", logo = "" },
            sale = new { allowCredit = false, defaultPayMethod = "微信" },
        });

        var r = await SettingSvc.SaveAsync(body);

        Assert.Equal(0, r.Code);
        Assert.Equal("新店名", Val(await Group("shop"), "name"));
        Assert.Equal("微信", Val(await Group("sale"), "defaultPayMethod"));

        var log = await Db.OperationLogs.AsNoTracking().OrderByDescending(l => l.Id).FirstAsync();
        Assert.Equal("系统管理", log.Module);
        Assert.Equal("保存系统设置", log.Action);
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingRow()
    {
        SetConfig("shop", """{"name":"旧店名"}""");

        var body = await AsJson(new { shop = new { name = "改后店名" } });
        await SettingSvc.SaveAsync(body);

        Assert.Equal(1, await Db.SystemConfigs.CountAsync(c => c.ConfigKey == "shop"));
        Assert.Equal("改后店名", Val(await Group("shop"), "name"));
    }

    [Fact]
    public async Task SaveAsync_NonObjectBody_ReturnsFail()
    {
        var r = await SettingSvc.SaveAsync(Json("[1,2,3]"));

        Assert.NotEqual(0, r.Code);
        Assert.Equal("参数格式错误", r.Message);
        Assert.Equal(0, await Db.SystemConfigs.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_NewBarcodeKey_EncryptedAtRest()
    {
        var body = await AsJson(new { barcode = new { apiZeroKey = "my-secret-key-9999" } });

        var r = await SettingSvc.SaveAsync(body);
        Assert.Equal(0, r.Code);

        var row = await Db.SystemConfigs.AsNoTracking().FirstAsync(c => c.ConfigKey == "barcode");
        var stored = JsonDocument.Parse(row.ConfigValue).RootElement.GetProperty("apiZeroKey").GetString();
        Assert.NotNull(stored);
        Assert.DoesNotContain("my-secret-key-9999", stored);                       // 库里必须是密文
        Assert.Equal("my-secret-key-9999", NewCipher().Decrypt(stored));           // 能解回明文
    }

    [Fact]
    public async Task SaveAsync_MaskedKey_KeepsOriginalCipher()
    {
        var cipher = NewCipher();
        var enc = cipher.Encrypt("original-key-1234");
        SetConfig("barcode", $$"""{"apiZeroKey":"{{enc}}"}""");

        // 前端回显脱敏值，用户没改 → 提交脱敏占位符
        var body = await AsJson(new { barcode = new { apiZeroKey = AesStringCipher.Mask("original-key-1234") } });
        await SettingSvc.SaveAsync(body);

        var row = await Db.SystemConfigs.AsNoTracking().FirstAsync(c => c.ConfigKey == "barcode");
        var stored = JsonDocument.Parse(row.ConfigValue).RootElement.GetProperty("apiZeroKey").GetString();
        Assert.Equal(enc, stored);
        Assert.Equal("original-key-1234", cipher.Decrypt(stored));
    }

    [Fact]
    public async Task SaveAsync_ClearBarcodeKey_RemovesValue()
    {
        SetConfig("barcode", $$"""{"apiZeroKey":"{{NewCipher().Encrypt("to-be-cleared")}}"}""");

        var body = await AsJson(new { barcode = new { apiZeroKey = "" } });
        await SettingSvc.SaveAsync(body);

        var row = await Db.SystemConfigs.AsNoTracking().FirstAsync(c => c.ConfigKey == "barcode");
        Assert.Equal("", JsonDocument.Parse(row.ConfigValue).RootElement.GetProperty("apiZeroKey").GetString());
        Assert.Equal("", Val(await Group("barcode"), "apiZeroKey"));
    }

    [Fact]
    public async Task SaveAsync_BarcodeGroupWithoutKey_TreatedAsEmpty()
    {
        var body = await AsJson(new { barcode = new { other = 1 } });

        var r = await SettingSvc.SaveAsync(body);

        Assert.Equal(0, r.Code);
        Assert.Equal("", Val(await Group("barcode"), "apiZeroKey"));
    }

    [Fact]
    public async Task SaveAsync_StockGroup_ReportExpiryDaysPickedUp()
    {
        var body = await AsJson(new { stock = new { warningThreshold = 9, expiryDays = 15 } });
        await SettingSvc.SaveAsync(body);

        Assert.Equal(15, await ReportSvc.GetExpiryDaysAsync());
    }

    // ==================== 备份/恢复 ====================

    [Fact]
    public void BackupDirPath_UsesContentRootBackups()
    {
        Assert.EndsWith("backups", SettingSvc.BackupDirPath);
        Assert.True(Path.IsPathRooted(SettingSvc.BackupDirPath));
    }

    [Fact]
    public async Task RestoreAsync_NonBakFile_ReturnsFail()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });

        var r = await SettingSvc.RestoreAsync(ms, "data.zip");

        Assert.NotEqual(0, r.Code);
        Assert.Equal("请上传 .bak 备份文件", r.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task RestoreAsync_EmptyFileName_ReturnsFail(string? name)
    {
        using var ms = new MemoryStream();

        var r = await SettingSvc.RestoreAsync(ms, name!);

        Assert.NotEqual(0, r.Code);
    }
}
