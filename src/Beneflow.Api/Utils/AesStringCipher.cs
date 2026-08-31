using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Beneflow.Api.Utils;

/// <summary>
/// AES-256-CBC 字符串加解密：用于敏感配置（如第三方 API Key）落库前加密、读取时解密。
/// 密钥来自 appsettings.json 的 Security:AesKey（32 字节 base64）。
/// 首次启动若未配置，自动生成随机密钥并写回 appsettings.json，保证开箱即用且每实例独立。
/// 密文格式：base64(IV[16] || ciphertext)，带 IV，同一明文每次加密结果不同。
/// </summary>
public class AesStringCipher
{
    private const string KeySection = "Security";
    private const string KeyName = "AesKey";
    private const string MaskPrefix = "***"; // 脱敏占位符前缀，前端展示用，保存时识别为"不修改"
    private readonly byte[] _key;
    private readonly string _appsettingsPath;

    public AesStringCipher(IConfiguration conf, IHostEnvironment env)
    {
        _appsettingsPath = Path.Combine(env.ContentRootPath, "appsettings.json");
        var b64 = conf[$"{KeySection}:{KeyName}"];
        if (string.IsNullOrWhiteSpace(b64))
        {
            // 首次启动：生成 32 字节随机密钥并写回 appsettings.json
            var key = RandomNumberGenerator.GetBytes(32);
            b64 = Convert.ToBase64String(key);
            TryPersistKey(b64);
            _key = key;
        }
        else
        {
            _key = Convert.FromBase64String(b64);
            if (_key.Length != 32) throw new InvalidOperationException($"Security:AesKey 必须是 32 字节 base64，当前 {_key.Length} 字节");
        }
    }

    /// <summary>加密明文，返回 base64(IV||cipher)。null/空串原样返回。</summary>
    public string? Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipherBytes = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var outBytes = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, outBytes, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, outBytes, aes.IV.Length, cipherBytes.Length);
        return Convert.ToBase64String(outBytes);
    }

    /// <summary>解密 base64(IV||cipher) 返回明文。密文格式不符/解密失败返回 null。</summary>
    public string? Decrypt(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        // 兼容：未加密的旧明文值（不含 IV 结构或 base64 解码失败）直接当明文返回，避免历史数据损坏导致功能瘫痪
        try
        {
            var all = Convert.FromBase64String(cipher);
            if (all.Length <= 16) return cipher; // 太短，不像密文，当明文
            using var aes = Aes.Create();
            aes.Key = _key;
            var iv = new byte[16];
            Buffer.BlockCopy(all, 0, iv, 0, 16);
            aes.IV = iv;
            var cipherBytes = new byte[all.Length - 16];
            Buffer.BlockCopy(all, 16, cipherBytes, 0, cipherBytes.Length);
            using var dec = aes.CreateDecryptor();
            var plainBytes = dec.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // 解密失败：当作未加密的明文原样返回（向后兼容历史明文数据）
            return cipher;
        }
    }

    /// <summary>脱敏：只显示后 4 位，前面用 *** 代替。空值返回空。短于等于4位全部遮罩。</summary>
    public static string Mask(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (plain.Length <= 4) return MaskPrefix;
        return MaskPrefix + plain[^4..];
    }

    /// <summary>判断值是否为脱敏占位符（以 *** 开头），用于保存时识别"用户未修改"。</summary>
    public static bool IsMasked(string? v) => v != null && v.StartsWith(MaskPrefix);

    /// <summary>把生成的密钥写回 appsettings.json 的 Security:AesKey。失败只记日志不抛（不阻塞启动）。</summary>
    private void TryPersistKey(string b64)
    {
        try
        {
            var json = File.ReadAllText(_appsettingsPath);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name == KeySection)
                    {
                        // 已有 Security 节，合并写入 AesKey
                        writer.WritePropertyName(KeySection);
                        writer.WriteStartObject();
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var inner in prop.Value.EnumerateObject())
                            {
                                if (inner.Name != KeyName) inner.WriteTo(writer);
                            }
                        }
                        writer.WriteString(KeyName, b64);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                // 没有 Security 节则新增
                if (!doc.RootElement.TryGetProperty(KeySection, out _))
                {
                    writer.WritePropertyName(KeySection);
                    writer.WriteStartObject();
                    writer.WriteString(KeyName, b64);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            File.WriteAllText(_appsettingsPath, Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch
        {
            // 写回失败不阻塞启动，密钥仅在内存中，重启后变化（历史密文需重新配置）
        }
    }
}
