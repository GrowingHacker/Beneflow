using System.Net;
using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 三个「要碰外部世界」的端点：条码联网反查、数据库全量备份、上传 .bak 恢复。
///
/// 它们的共同点是**没法在 InMemory 进程内环境里跑完整链路**：
/// 条码反查要打第三方 API（Open Food Facts / ApiZero），备份恢复要真的连 SQL Server 跑 BACKUP/RESTORE。
/// 所以这里只守「确定性可验证的前半段」—— 鉴权、入参校验、以及「外部源不可用时不能变成 500」；
/// 真实的备份产出与恢复由**真库验收跑批**覆盖（本机工具，不入库），那边才有 SQL Server。
///
/// 之所以专门起一个类：这些端点都是**幂等失败**的（失败不改数据），
/// 但也都不该和业务用例挤在同一个共享库里，免得将来有人给它们加上写数据的断言。
///
/// ⚠️ 踩到的坑（已记进项目笔记的已知缺口）：<c>POST /api/v1/settings/restore</c> 带 IFormFile 参数，
/// <c>[ApiController]</c> 会据此推断 <c>Consumes("multipart/form-data")</c>；
/// 所以「不带表单体 POST 这个路径」匹配不到该端点，请求会落进 <c>MapFallback</c> 的 SPA 兜底，
/// 拿到 <c>index.html</c> + HTTP 200 —— 既不是 401 也不是 415。
/// 真实客户端永远发 multipart，所以只影响手搓请求；本文件按真实形态发请求，不去断言这个兜底行为。
/// </summary>
public class OpsEndpointsIntegrationTests : IntegrationSeedBase
{
    public OpsEndpointsIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    [Fact]
    public async Task 运维端点未登录全部401()
    {
        // 备份/恢复/清库是三个「一按就动全库」的按钮，鉴权漏一个都是致命的。
        //
        // ⚠️ /settings/restore 必须用**真实的 multipart 请求**来探：
        // 它带 IFormFile 参数，[ApiController] 会据此推断出 Consumes("multipart/form-data")，
        // 于是「不带表单体的 POST」根本匹配不到这个端点，请求会落到 SPA 兜底路由 ——
        // 返回的是 index.html + HTTP 200，既不是 401 也不是 415（已记进已知缺口，不在这里断言它）。
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "x.bak");

        var cases = new (string Label, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("GET  /api/v1/barcode/lookup/{code}", () => Client.GetAsync("/api/v1/barcode/lookup/6901234567892")),
            ("POST /api/v1/settings/backup", () => Client.PostAsync("/api/v1/settings/backup", null)),
            ("POST /api/v1/settings/restore", () => Client.PostAsync("/api/v1/settings/restore", form)),
            ("POST /api/v1/settings/demo-data/clear", () => Client.PostAsync("/api/v1/settings/demo-data/clear", null)),
        };

        foreach (var (label, send) in cases)
        {
            var resp = await send();
            Assert.True(resp.StatusCode == HttpStatusCode.Unauthorized,
                $"{label} 未登录应返回 401，实际 {(int)resp.StatusCode}");
        }
    }

    [Fact]
    public async Task 条码联网反查_条码长度不足直接拒绝()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await Client.GetAsync("/api/v1/barcode/lookup/12345"));

        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("长度不足", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 条码联网反查_外部源不可用或未命中时返回空而不是500()
    {
        await LoginAsAdminAsync();
        // 用一个人为构造的、第三方库里不可能存在的条码。
        // 两个源都失败（断网/超时）或都未命中时，服务必须回 Ok(null) 让前端弹空白表单，
        // **不能变成 500** —— 否则收银台在断网时连扫描枪都用不了。
        // 代价：断网时这条用例最坏要等两个源各 2.5s 超时。
        var resp = await Client.GetAsync("/api/v1/barcode/lookup/99999999999999");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await ReadBody(resp);
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        // 命中与否取决于外部源，不作断言；只要求形状正确（null 或一个对象）
        Assert.True(body.GetProperty("data").ValueKind is JsonValueKind.Null or JsonValueKind.Object);
    }

    [Fact]
    public async Task 上传恢复_不是bak文件时拒绝且不碰数据库()
    {
        await LoginAsAdminAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("not a real backup"u8.ToArray()), "file", "某个备份.txt");

        var body = await ReadBody(await Client.PostAsync("/api/v1/settings/restore", form));

        // 文件名校验在连 SQL Server 之前，所以这条用例在 InMemory 下也能真跑
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains(".bak", body.GetProperty("message").GetString());
    }
}
