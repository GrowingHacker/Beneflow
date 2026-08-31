using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beneflow.Api.Data;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>
/// 条形码联网查询服务：双源聚合（Open Food Facts → ApiZero），
/// 本地商品库无此条码时可尝试从网络反查名称/分类/规格/单价等，
/// 结果内存缓存 7 天，避免重复请求第三方造成限流。
/// ApiZero API Key 在「系统设置 → 条码查询」页面维护，存 SystemConfig 表 barcode 组；
/// 未设置时 appsettings.json 作兜底，留空则匿名调用。
/// </summary>
public class BarcodeService : IBarcodeService
{
    private static readonly JsonSerializerOptions _jsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<BarcodeService> _log;
    private readonly AppDbContext _db;
    private readonly IConfiguration _conf;
    private readonly AesStringCipher _cipher;
    private static readonly ConcurrentDictionary<string, (BarcodeInfo? info, DateTime expireAt)> _cache = new();
    private const int CacheHours = 7 * 24;
    private const int TimeoutMs = 2500; // 每个源单次最多 2.5 秒，避免阻塞前端

    // API Key 动态缓存：从 SystemConfig 表 barcode 组读取，30 秒内复用，避免每条码查都打 DB
    private static readonly SemaphoreSlim _keyLock = new(1, 1);
    private static (string apiZero, DateTime at) _keyCache = default;

    public BarcodeService(IHttpClientFactory http, ILogger<BarcodeService> log, IConfiguration conf, AppDbContext db, AesStringCipher cipher)
    {
        _http = http;
        _log = log;
        _conf = conf;
        _db = db;
        _cipher = cipher;
    }

    /// <summary>加载 ApiZero API Key：数据库(系统设置) > appsettings.json。留空=匿名。30 秒缓存。</summary>
    private async Task<string> LoadApiZeroKeyAsync()
    {
        var c = _keyCache;
        if (c.at != default && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(30))
            return c.apiZero;

        await _keyLock.WaitAsync();
        try
        {
            c = _keyCache;
            if (c.at != default && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(30))
                return c.apiZero;

            string dbKey = "";
            try
            {
                var row = await _db.SystemConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.ConfigKey == "barcode");
                if (row != null)
                {
                    using var doc = JsonDocument.Parse(row.ConfigValue);
                    if (doc.RootElement.TryGetProperty("apiZeroKey", out var az) && az.ValueKind == JsonValueKind.String)
                        dbKey = _cipher.Decrypt(az.GetString()) ?? "";   // 库内存密文，读取时解密
                }
            }
            catch (Exception e) { _log.LogDebug(e, "读取 barcode 系统设置失败，回退兜底"); }

            var apiZero = NonEmpty(dbKey, _conf["Barcode:ApiZeroKey"]);
            _keyCache = (apiZero, DateTime.UtcNow);
            return apiZero;
        }
        finally { _keyLock.Release(); }
    }

    /// <summary>取第一个非空白字符串并 Trim，全空返回 ""。</summary>
    private static string NonEmpty(params string?[] vals)
    {
        foreach (var v in vals)
            if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
        return "";
    }

    /// <summary>按条码联网反查商品信息，查不到返回 null。</summary>
    public async Task<BarcodeInfo?> LookupAsync(string code)
    {
        code = code?.Trim() ?? "";
        if (code.Length < 6) return null;

        // 顺带清理已过期的缓存项，防止长期运行下字典无界增长
        foreach (var kv in _cache)
            if (kv.Value.expireAt <= DateTime.UtcNow) _cache.TryRemove(kv.Key, out _);

        // 1. 内存缓存命中
        if (_cache.TryGetValue(code, out var c) && c.expireAt > DateTime.UtcNow)
            return c.info;

        BarcodeInfo? result = null;
        bool offTried = false, azTried = false;

        // 2. 源 1：ApiZero（国内消费品千万 SKU，注册即用免审核，国产条码覆盖好）
        //    匿名 20次/天 QPS=1；填 Key 200次/天 QPS=2。Key 留空也能调，所以始终优先尝试。
        try
        {
            result = await QueryApiZeroAsync(code);
            azTried = true;
            if (result != null) _log.LogInformation("条码 {Code} 通过 ApiZero 命中：{Name}", code, result.Name);
        }
        catch (Exception e) { _log.LogDebug(e, "ApiZero 查询条码 {Code} 失败", code); }

        // 3. 源 2：Open Food Facts（国际，免费，进口食品类高命中）—— ApiZero 未命中时兜底
        if (result == null)
        {
            try
            {
                result = await QueryOpenFoodFactsAsync(code);
                offTried = true;
                if (result != null) _log.LogInformation("条码 {Code} 通过 OpenFoodFacts 命中：{Name}", code, result.Name);
            }
            catch (Exception e) { _log.LogDebug(e, "OpenFoodFacts 查询条码 {Code} 失败", code); }
        }

        // 4. 写入缓存：
        //    - 命中：缓存 7 天
        //    - 两个源都跑完了还没命中：空值缓存 10 分钟
        //    - 中途网络异常/超时（没真正跑完任一源）：不缓存，下次还有机会重试
        TimeSpan? ttl = null;
        if (result != null) ttl = TimeSpan.FromHours(CacheHours);
        else if (offTried && azTried) ttl = TimeSpan.FromMinutes(10);

        if (ttl.HasValue)
            _cache[code] = (result, DateTime.UtcNow.Add(ttl.Value));

        return result;
    }

    // ---------------- Open Food Facts ----------------
    // OFF 对于不存在的 / 演示用条码会返回一个「占位假商品」，典型特征：
    //   name = "Test product API"  / brands = "BACKBOX" / categories 恒等于 "Breads"
    // 这里按命中特征过滤掉，避免把假数据当成真实命中。
    private static readonly HashSet<string> OffDummyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test product API", "Test Product API", "test product api", "Api test product", "Demo product"
    };
    private static readonly HashSet<string> OffDummyBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "BACKBOX", "Backbox", "TEST", "DEMO"
    };
    private static readonly HashSet<string> OffDummyCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Breads", "Bread", "Dummy", "Test"
    };
    private bool IsOffDummy(string? name, string? brands, string? firstCategory)
    {
        if (name != null && OffDummyNames.Contains(name.Trim())) return true;
        if (brands != null && OffDummyBrands.Contains(brands.Trim())) return true;
        if (firstCategory != null && OffDummyCategories.Contains(firstCategory.Trim())) return true;
        // brand 含 test/demo/backbox 子串 → 占位（TestMarke / BACKBOX / TEST 等通用，真实品牌不含这些词）
        if (brands != null)
        {
            var b = brands.ToLowerInvariant();
            if (b.Contains("test") || b.Contains("demo") || b.Contains("backbox")) return true;
        }
        // 名称/品牌里同时有 test+api 这种组合的，直接视为 OFF 官方占位对象
        var tag = $"{name}|{brands}".ToLowerInvariant();
        if (tag.Contains("test product") && tag.Contains("api")) return true;
        return false;
    }

    private async Task<BarcodeInfo?> QueryOpenFoodFactsAsync(string code)
    {
        using var cts = new CancellationTokenSource(TimeoutMs);
        var client = _http.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Beneflow/1.0 (contact@beneflow.local)");
        var url = $"https://world.openfoodfacts.org/api/v0/product/{Uri.EscapeDataString(code)}.json";
        var json = await client.GetStringAsync(url, cts.Token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("status", out var st) && st.GetInt32() != 1) return null;
        if (!root.TryGetProperty("product", out var p) || p.ValueKind != JsonValueKind.Object) return null;

        // 按优先级抽：中文简体 > 中文通用 > 通用 > 缩写 > 品牌通用
        var name =
            P_str(p, "product_name_zh_CN", "product_name_zh-CN") ??
            P_str(p, "product_name_zh") ??
            P_str(p, "product_name") ??
            P_str(p, "abbreviated_product_name");
        var brands = P_str(p, "brands");
        var categories =
            P_str(p, "categories_zh_CN", "categories_zh-CN") ??
            P_str(p, "categories_zh") ??
            P_str(p, "categories");
        var quantity = P_str(p, "quantity") ?? P_str(p, "net_weight");
        var img = P_str(p, "image_small_url") ?? P_str(p, "image_url");

        if (string.IsNullOrWhiteSpace(name)) return null;

        var firstCat = SplitFirst(categories);
        // 过滤 OFF 官方占位假数据
        if (IsOffDummy(name, brands, firstCat))
        {
            _log.LogDebug("条码 {Code} OFF 返回占位假数据（name={Name}, brand={Brand}），当作未命中", code, name, brands);
            return null;
        }

        var info = new BarcodeInfo
        {
            Barcode = code,
            Name = name.Trim(),
            Brand = brands,
            Category = firstCat,
            Unit = GuessUnit(quantity) ?? "件",
            Spec = quantity,
            ImageUrl = img,
            Source = "OpenFoodFacts",
            SuggestedSalePrice = null,
            SuggestedCostPrice = null
        };
        return info;
    }

    /// <summary>从 JsonElement 按候选键取第一个非空字符串。</summary>
    private static string? P_str(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    // ---------------- apizero.cn 商品条码查询-免费版（国内消费品，注册即用免审核）----------------
    // 文档：https://apizero.cn/marketplace/barcode-lookup
    // 接口：GET https://v1.apizero.cn/api/barcode-lookup?barcode={code}
    // 鉴权：Bearer <Key>（可空，匿名 20次/天 QPS=1；填 Key 200次/天 QPS=2）
    // 关键判定：外层 code=0 仅代表网络成功；data.found=true 才是真命中。未命中 found=false 字段全 null。
    // 异常：429 限流（匿名 QPS 超 1 / 当日额度超）→ 抛 HttpRequestException 让上层不缓存
    private async Task<BarcodeInfo?> QueryApiZeroAsync(string code)
    {
        var apiZeroKey = await LoadApiZeroKeyAsync();
        using var cts = new CancellationTokenSource(TimeoutMs);
        var client = _http.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Beneflow/1.0 (contact@beneflow.local)");
        if (!string.IsNullOrWhiteSpace(apiZeroKey))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiZeroKey);

        var url = $"https://v1.apizero.cn/api/barcode-lookup?barcode={Uri.EscapeDataString(code)}";
        var resp = await client.GetAsync(url, cts.Token);
        // 429 限流：不当作"未命中"，抛异常让上层跳过缓存写入，下次还能重试
        if ((int)resp.StatusCode == 429)
        {
            _log.LogWarning("ApiZero 限流（429）— 匿名 QPS=1 易触发，建议在 appsettings.json 配置 Barcode:ApiZeroKey 提升到 200次/天 QPS=2");
            throw new HttpRequestException("ApiZero 429 Too Many Requests");
        }
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogDebug("ApiZero HTTP {Status} body={Body}", (int)resp.StatusCode, json[..Math.Min(json.Length, 200)]);
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 外层 code=0 仅网络成功；非 0 一般是参数错（条码位数不对等）
        if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() != 0)
        {
            var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
            _log.LogDebug("ApiZero 业务失败 code={Code} msg={Msg}", c.GetInt32(), msg);
            return null;
        }
        if (!root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;

        // found=false 是正常的"库里无此条码"，当作未命中（不抛错，让上层缓存空值）
        if (d.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.False) return null;

        var name = P_str(d, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var spec = P_str(d, "spec");
        decimal? price = null;
        if (d.TryGetProperty("price", out var pv) && pv.ValueKind == JsonValueKind.Number)
            price = Math.Round(pv.GetDecimal(), 2);
        else if (decimal.TryParse(P_str(d, "price"), out var pp)) price = Math.Round(pp, 2);

        return new BarcodeInfo
        {
            Barcode = code,
            Name = name,
            Brand = P_str(d, "brand"),
            Category = P_str(d, "category"),
            Unit = GuessUnit(spec) ?? "件",
            Spec = spec,
            ImageUrl = P_str(d, "image"),
            Source = "ApiZero",
            SuggestedSalePrice = price,
            SuggestedCostPrice = null
        };
    }

    // ---------------- 工具 ----------------
    private static string? FirstNonEmpty(params string?[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string? SplitFirst(string? raw, int idx = 0)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var arr = raw.Split(new[] { ',', '，', '/', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
        if (idx >= arr.Length) return null;
        var v = arr[idx].Trim();
        return StripLangTag(v);
    }

    /// <summary>去掉 OFF 的分类语言前缀，如 "en:Produits à tartiner" → "Produits à tartiner"。</summary>
    private static string? StripLangTag(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        var v = val.Trim();
        // "xx:" 形式（en/zh/fr 等两字母语言码）
        if (v.Length > 3 && v[2] == ':' && char.IsLetter(v[0]) && char.IsLetter(v[1]))
            return v[3..].Trim();
        // "xx-YY:" 形式（如 pt-BR、zh-Hans）
        if (v.Length > 5 && v[4] == ':')
        {
            bool ok = true;
            for (int i = 0; i < 4; i++)
            {
                var c = v[i];
                if (!(char.IsLetter(c) || c == '-')) { ok = false; break; }
            }
            if (ok) return v[5..].Trim();
        }
        return v;
    }

    private static string? GuessUnit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "件";
        var t = text;
        if (t.Contains("ml") || t.Contains("升") || t.Contains("L")) return "瓶";
        if (t.Contains("g") || t.Contains("kg") || t.Contains("克") || t.Contains("斤")) return "袋";
        if (t.Contains("包") || t.Contains("袋") || t.Contains("盒") || t.Contains("罐")
            || t.Contains("支") || t.Contains("瓶") || t.Contains("条") || t.Contains("卷"))
        {
            foreach (var ch in new[] { "包", "袋", "盒", "罐", "支", "瓶", "条", "卷" })
                if (t.Contains(ch)) return ch;
        }
        return "件";
    }
}

/// <summary>条码查询结果。</summary>
public class BarcodeInfo
{
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public string? Spec { get; set; }
    public string Unit { get; set; } = "件";
    public string? ImageUrl { get; set; }
    public string Source { get; set; } = "";
    public decimal? SuggestedSalePrice { get; set; }
    public decimal? SuggestedCostPrice { get; set; }
    [JsonIgnore] public bool HasName => !string.IsNullOrWhiteSpace(Name);
}

// ---------- 第三方 API 响应反序列化结构（只取需要字段）----------

#region Open Food Facts DTO
file class OffResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("product")] public OffProduct? Product { get; set; }
}
file class OffProduct
{
    [JsonPropertyName("product_name")] public string? ProductName { get; set; }
    [JsonPropertyName("product_name_zh_CN")] public string? ProductNameZhCn { get; set; }
    [JsonPropertyName("abbreviated_product_name")] public string? AbbreviatedProductName { get; set; }
    [JsonPropertyName("brands")] public string? Brands { get; set; }
    [JsonPropertyName("categories")] public string? Categories { get; set; }
    [JsonPropertyName("categories_zh_CN")] public string? CategoriesZhCn { get; set; }
    [JsonPropertyName("categories_tags")] public string? CategoriesTags { get; set; }
    [JsonPropertyName("quantity")] public string? Quantity { get; set; }
    [JsonPropertyName("net_weight")] public string? NetWeight { get; set; }
    [JsonPropertyName("image_small_url")] public string? ImageSmallUrl { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
}
#endregion

#region Mxnzp DTO
file class MxnzpResponse
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("data")] public MxnzpData? Data { get; set; }
}
file class MxnzpData
{
    [JsonPropertyName("goodsName")] public string? GoodsName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("spec")] public string? Spec { get; set; }
    [JsonPropertyName("specification")] public string? Specification { get; set; }
    [JsonPropertyName("trademark")] public string? Trademark { get; set; }
    [JsonPropertyName("enterpriseName")] public string? EnterpriseName { get; set; }
    [JsonPropertyName("categoryName")] public string? CategoryName { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("barCode")] public string? BarCode { get; set; }
}
#endregion
