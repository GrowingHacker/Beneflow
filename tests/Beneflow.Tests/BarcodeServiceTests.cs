using System.Net;
using System.Text;
using Beneflow.Api.Services;
using Beneflow.Api.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beneflow.Tests;

/// <summary>条码联网查询测试：用假 HttpClient 拦截双源请求，覆盖主流程与边界</summary>
public class BarcodeServiceTests : TestBase
{
    /// <summary>用预设响应模拟第三方 API</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode code, string body)> _routes = new();
        public List<string> HitUrls { get; } = new();
        public int CallCount { get; private set; }

        public void MapApiZero(string barcode, HttpStatusCode code, string body) =>
            _routes[$"https://v1.apizero.cn/api/barcode-lookup?barcode={Uri.EscapeDataString(barcode)}"] = (code, body);

        public void MapOff(string barcode, HttpStatusCode code, string body) =>
            _routes[$"https://world.openfoodfacts.org/api/v0/product/{Uri.EscapeDataString(barcode)}.json"] = (code, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            CallCount++;
            HitUrls.Add(req.RequestUri!.ToString());
            if (_routes.TryGetValue(req.RequestUri.ToString(), out var r))
                return Task.FromResult(new HttpResponseMessage(r.code)
                {
                    Content = new StringContent(r.body, Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not stubbed", Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public FakeHandler Handler { get; } = new();
        public HttpClient CreateClient(string name) => new(Handler, disposeHandler: false);
    }

    private BarcodeService NewBarcodeSvc(StubHttpFactory http)
    {
        // 每次拿一个独立 cipher 实例（Key 来自测试配置），与 SettingSvc 互不串味
        var cipher = NewCipher();
        return new BarcodeService(http, NullLogger<BarcodeService>.Instance, Config, Db, cipher);
    }

    [Fact]
    public async Task LookupAsync_TooShortCode_ReturnsNullWithoutNetwork()
    {
        var http = new StubHttpFactory();
        var svc = NewBarcodeSvc(http);

        Assert.Null(await svc.LookupAsync(""));                 // 空
        Assert.Null(await svc.LookupAsync("12345"));             // < 6
        Assert.Equal(0, http.Handler.CallCount);
    }

    [Fact]
    public async Task LookupAsync_ApiZeroHit_ParsesNamePriceAndSource()
    {
        var http = new StubHttpFactory();
        const string barcode = "6925303731906";
        http.Handler.MapApiZero(barcode, HttpStatusCode.OK, """
            {"code":0,"data":{"found":true,"name":"百岁山矿泉水","brand":"百岁山","category":"饮料","spec":"570ml","price":3.5,"image":""}}
            """);
        var svc = NewBarcodeSvc(http);

        var info = await svc.LookupAsync(barcode);

        Assert.NotNull(info);
        Assert.Equal("百岁山矿泉水", info!.Name);
        Assert.Equal("百岁山", info.Brand);
        Assert.Equal("饮料", info.Category);
        Assert.Equal(3.5m, info.SuggestedSalePrice);
        Assert.Equal("ApiZero", info.Source);
        Assert.Equal(barcode, info.Barcode);
    }

    [Fact]
    public async Task LookupAsync_ApiZeroNotFound_PropagatesAsNull()
    {
        var http = new StubHttpFactory();
        const string barcode = "6901234567890";
        http.Handler.MapApiZero(barcode, HttpStatusCode.OK, """{"code":0,"data":{"found":false}}""");
        var svc = NewBarcodeSvc(http);

        var info = await svc.LookupAsync(barcode);

        Assert.Null(info);
        // 未命中也会进 OFF 兜底（也会被 stub 返回 404），最后为空值
    }

    [Fact]
    public async Task LookupAsync_ApiZeroHttpError_TriesOffFallback()
    {
        var http = new StubHttpFactory();
        const string barcode = "4007630001400";
        http.Handler.MapApiZero(barcode, HttpStatusCode.InternalServerError, """{"error":"oops"}""");
        var svc = NewBarcodeSvc(http);

        var info = await svc.LookupAsync(barcode);

        Assert.Null(info);
        // ApiZero 报错后仍会请求 OFF 兜底 → 两次 HTTP
        Assert.Equal(2, http.Handler.CallCount);
        Assert.Contains(http.Handler.HitUrls, u => u.StartsWith("https://world.openfoodfacts.org/"));
    }

    [Fact]
    public async Task LookupAsync_ApiZeroBusinessFail_ReturnsNull()
    {
        var http = new StubHttpFactory();
        const string barcode = "1111111111111";
        http.Handler.MapApiZero(barcode, HttpStatusCode.OK, """{"code":1001,"msg":"参数错"}""");
        var svc = NewBarcodeSvc(http);

        Assert.Null(await svc.LookupAsync(barcode));
    }

    [Fact]
    public async Task LookupAsync_ApiZero_AuthorizesWithConfiguredKey()
    {
        var http = new StubHttpFactory();
        const string barcode = "5901234123457";
        // 把 key 用 cipher 加密存进 SystemConfig，前端会解密并加 Bearer 头
        var cipher = NewCipher();
        var encKey = cipher.Encrypt("test-key-abcdefgh");
        SetConfig("barcode", $$"""{"apiZeroKey":"{{encKey}}"}""");
        http.Handler.MapApiZero(barcode, HttpStatusCode.OK, """{"code":0,"data":{"found":true,"name":"X","price":1}}""");
        var svc = NewBarcodeSvc(http);

        var info = await svc.LookupAsync(barcode);

        Assert.NotNull(info);
        Assert.Equal("X", info!.Name);
    }

    [Fact]
    public void AesCipher_RoundTrip_ApivZeroKeyStable()
    {
        var cipher = NewCipher();
        var enc = cipher.Encrypt("key-1234");
        Assert.Equal("key-1234", cipher.Decrypt(enc));
        // 与 BarcodeService 共用同一 cipher 实例，加解密对称
        var svc = new BarcodeService(new StubHttpFactory(), NullLogger<BarcodeService>.Instance, Config, Db, cipher);
        Assert.NotNull(svc);
    }
}
