using System.Text.Json;
using Beneflow.Api.Models;
using Beneflow.Api.Utils;

namespace Beneflow.Tests;

/// <summary>纯工具类测试：密码哈希、拼音码、AES 加解密/脱敏、统一响应包装</summary>
public class UtilsTests : TestBase
{
    // ==================== 密码哈希 ====================

    [Fact]
    public void PasswordHasher_Verify_RejectsWrongPassword()
    {
        var salt = PasswordHasher.NewSalt();
        var hash = PasswordHasher.Hash("123456", salt);

        Assert.True(PasswordHasher.Verify("123456", salt, hash));
        Assert.False(PasswordHasher.Verify("1234567", salt, hash));
        Assert.False(PasswordHasher.Verify("", salt, hash));
    }

    [Fact]
    public void PasswordHasher_Salt_IsUniquePerCall()
    {
        Assert.NotEqual(PasswordHasher.NewSalt(), PasswordHasher.NewSalt());
    }

    [Fact]
    public void PasswordHasher_Hash_DiffersAcrossSalts()
    {
        var s1 = PasswordHasher.NewSalt();
        var s2 = PasswordHasher.NewSalt();

        Assert.NotEqual(PasswordHasher.Hash("123456", s1), PasswordHasher.Hash("123456", s2));
    }

    [Fact]
    public void PasswordHasher_Verify_EmptyOrBrokenInput_ReturnsFalse()
    {
        Assert.False(PasswordHasher.Verify("123456", "", "abc"));
        Assert.False(PasswordHasher.Verify("123456", "abc", ""));
        Assert.False(PasswordHasher.Verify("123456", "zzz", "not-hex"));   // 非法十六进制被吞
    }

    [Fact]
    public void PasswordHasher_Hash_IsLowercaseHex()
    {
        var hash = PasswordHasher.Hash("123456", PasswordHasher.NewSalt());

        Assert.Equal(64, hash.Length);                                     // 32 字节 → 64 hex
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    // ==================== 拼音码 ====================

    [Theory]
    [InlineData("花生米", "HSM")]
    [InlineData("矿泉水", "KQS")]
    [InlineData("可乐", "KL")]
    [InlineData("康师傅红烧牛肉面", "KSFHSNRM")]
    public void PinyinHelper_ChineseName_ReturnsFirstLetters(string name, string expected) =>
        Assert.Equal(expected, PinyinHelper.GetPinyinCode(name));

    [Fact]
    public void PinyinHelper_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", PinyinHelper.GetPinyinCode(null));
        Assert.Equal("", PinyinHelper.GetPinyinCode(""));
        Assert.Equal("", PinyinHelper.GetPinyinCode("   "));
    }

    [Fact]
    public void PinyinHelper_KeepsAsciiLettersAndDigits_Uppercased()
    {
        Assert.Equal("CCTV1", PinyinHelper.GetPinyinCode("CCTV1"));
        Assert.Equal("ABC", PinyinHelper.GetPinyinCode("abc"));
    }

    [Fact]
    public void PinyinHelper_MixedName_KeepsBothParts() =>
        Assert.Equal("KL500ML", PinyinHelper.GetPinyinCode("可乐500ml"));

    [Fact]
    public void PinyinHelper_LongName_TruncatedTo20() =>
        Assert.True(PinyinHelper.GetPinyinCode("康师傅红烧牛肉面香菇炖鸡面老坛酸菜牛肉面").Length <= 20);

    // ==================== AES 加解密 ====================

    [Fact]
    public void AesCipher_RoundTrip_RecoversPlainText()
    {
        var cipher = NewCipher();

        var enc = cipher.Encrypt("hello-世界-123");
        Assert.NotEqual("hello-世界-123", enc);
        Assert.Equal("hello-世界-123", cipher.Decrypt(enc));
    }

    [Fact]
    public void AesCipher_SamePlainText_ProducesDifferentCipher()
    {
        var cipher = NewCipher();

        Assert.NotEqual(cipher.Encrypt("same"), cipher.Encrypt("same"));   // 随机 IV
    }

    [Fact]
    public void AesCipher_EmptyValue_Passthrough()
    {
        var cipher = NewCipher();

        Assert.Equal("", cipher.Encrypt(""));
        Assert.Null(cipher.Encrypt(null));
        Assert.Equal("", cipher.Decrypt(""));
        Assert.Null(cipher.Decrypt(null));
    }

    [Fact]
    public void AesCipher_Decrypt_LegacyPlainText_ReturnedAsIs()
    {
        var cipher = NewCipher();

        Assert.Equal("legacy-plain-key", cipher.Decrypt("legacy-plain-key"));
    }

    [Fact]
    public void AesCipher_Mask_ShowsLastFourOnly()
    {
        Assert.Equal("", AesStringCipher.Mask(""));
        Assert.Equal("", AesStringCipher.Mask(null));
        Assert.Equal("***", AesStringCipher.Mask("1234"));                 // ≤4 位全遮
        Assert.Equal("***", AesStringCipher.Mask("ab"));
        Assert.Equal("***6789", AesStringCipher.Mask("123456789"));
    }

    [Theory]
    [InlineData("***6789", true)]
    [InlineData("***", true)]
    [InlineData("123456789", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AesCipher_IsMasked_DetectsPlaceholder(string? v, bool expected) =>
        Assert.Equal(expected, AesStringCipher.IsMasked(v));

    // ==================== 统一响应 ====================

    [Fact]
    public void ApiResult_Ok_HasZeroCodeAndPayload()
    {
        var r = ApiResult.Ok(new { a = 1 });

        Assert.Equal(0, r.Code);
        Assert.Equal("success", r.Message);
        Assert.NotNull(r.Data);
        Assert.Equal(1, Prop<int>(r.Data, "a"));
    }

    [Fact]
    public void ApiResult_Fail_HasNonZeroCodeAndMessage()
    {
        var r = ApiResult.Fail("库存不足");

        Assert.Equal(1, r.Code);
        Assert.Equal("库存不足", r.Message);
        Assert.Null(r.Data);
    }

    [Fact]
    public void ApiResult_Fail_CustomCodeRespected()
    {
        var r = ApiResult.Fail("未授权", 401);

        Assert.Equal(401, r.Code);
    }

    [Fact]
    public void ApiResult_Generic_SerializesToCodeMessageData()
    {
        var json = JsonSerializer.Serialize(ApiResult<int>.Ok(5),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("data").GetInt32());
    }

    [Fact]
    public void PagedResult_Defaults_AreEmpty()
    {
        var p = new PagedResult<string>();

        Assert.Empty(p.List);
        Assert.Equal(0, p.Total);
        Assert.Equal(0, p.Page);
        Assert.Equal(0, p.PageSize);
    }
}
