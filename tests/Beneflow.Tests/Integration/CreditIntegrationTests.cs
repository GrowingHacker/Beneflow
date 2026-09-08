using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 赊账（应收账款）集成测试：验证「赊账销售创建欠条 → 赊账列表可见 → 结清还款（全额/重复拦截）→ 修改手机号备注」
/// 整条链路，端到端经由真实 HTTP 管线。
/// </summary>
public class CreditIntegrationTests : IntegrationTestBase
{
    public CreditIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    /// <summary>创建一笔赊账销售，返回其赊账记录 Id、微信号与欠款金额。</summary>
    private async Task<(int creditId, string wechatId, decimal amount)> CreateCreditSaleAsync(
        string name, decimal price, decimal stock, decimal qty)
    {
        var cats = await ReadBody(await Client.GetAsync("/api/v1/categories"));
        var catId = cats.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
        var prodResp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "", name, categoryId = catId, unit = "瓶", salePrice = price, costPrice = price - 2,
            stockQuantity = stock, safetyStock = 0, hasExpiry = false, shelfLifeDays = 0,
            isWeighted = false, status = true,
        });
        var productId = (await ExpectOk(prodResp)).GetProperty("id").GetInt32();

        var wechatId = "ITWX_" + Guid.NewGuid().ToString("N")[..8];
        var amount = Math.Round(qty * price, 2);
        var saleResp = await PostJsonAsync("/api/v1/sales", new
        {
            items = new[] { new { productId, qty, unitPrice = price, subTotal = amount } },
            totalAmount = amount, discountAmount = 0m, payAmount = amount,
            payMethod = "赊账", isCredit = true, wechatId,
        });
        var saleData = await ExpectOk(saleResp);
        Assert.True(saleData.GetProperty("id").GetInt32() > 0);

        var listBody = await ReadBody(await Client.GetAsync($"/api/v1/credits?keyword={wechatId}"));
        var credit = listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("wechatId").GetString() == wechatId);
        var creditId = credit.GetProperty("id").GetInt32();
        return (creditId, wechatId, amount);
    }

    private async Task<JsonElement> GetCreditAsync(string wechatId)
    {
        var listBody = await ReadBody(await Client.GetAsync($"/api/v1/credits?keyword={wechatId}"));
        return listBody.GetProperty("data").GetProperty("list").EnumerateArray()
            .First(x => x.GetProperty("wechatId").GetString() == wechatId);
    }

    [Fact]
    public async Task Credit_SaleCreatesUnpaidCredit_ShowsInList()
    {
        await LoginAsAdminAsync();
        var name = "ITCR_" + Guid.NewGuid().ToString("N")[..8];
        var (creditId, wechatId, amount) = await CreateCreditSaleAsync(name, 12m, 10, 3);

        var credit = await GetCreditAsync(wechatId);
        Assert.Equal(creditId, credit.GetProperty("id").GetInt32());
        Assert.Equal("未结清", credit.GetProperty("status").GetString());
        Assert.Equal(amount, credit.GetProperty("remainingAmount").GetDecimal());
        Assert.Equal(amount, credit.GetProperty("creditAmount").GetDecimal());
    }

    [Fact]
    public async Task Credit_Settle_FullySettles_ReflectsInList()
    {
        await LoginAsAdminAsync();
        var name = "ITCR2_" + Guid.NewGuid().ToString("N")[..8];
        var (creditId, wechatId, amount) = await CreateCreditSaleAsync(name, 12m, 10, 3);

        var settleResp = await PostJsonAsync($"/api/v1/credits/{creditId}/settle",
            new { payAmount = amount, payMethod = "微信" });
        await ExpectOk(settleResp);

        var credit = await GetCreditAsync(wechatId);
        Assert.Equal("已结清", credit.GetProperty("status").GetString());
        Assert.Equal(0m, credit.GetProperty("remainingAmount").GetDecimal());
    }

    [Fact]
    public async Task Credit_SettleAlreadySettled_Fails()
    {
        await LoginAsAdminAsync();
        var name = "ITCR3_" + Guid.NewGuid().ToString("N")[..8];
        var (creditId, wechatId, amount) = await CreateCreditSaleAsync(name, 12m, 10, 3);

        await ExpectOk(await PostJsonAsync($"/api/v1/credits/{creditId}/settle",
            new { payAmount = amount, payMethod = "微信" }));

        // 已结清再结清应失败
        var again = await PostJsonAsync($"/api/v1/credits/{creditId}/settle",
            new { payAmount = 1m, payMethod = "微信" });
        var body = await ReadBody(again);
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
        Assert.Contains("已结清", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Credit_UpdatePhoneRemark_Succeeds()
    {
        await LoginAsAdminAsync();
        var name = "ITCR4_" + Guid.NewGuid().ToString("N")[..8];
        var (creditId, wechatId, _) = await CreateCreditSaleAsync(name, 12m, 10, 3);

        var updResp = await PutJsonAsync($"/api/v1/credits/{creditId}",
            new { phone = "13800138000", remark = "月底结清" });
        await ExpectOk(updResp);

        var credit = await GetCreditAsync(wechatId);
        Assert.Equal("13800138000", credit.GetProperty("phone").GetString());
        Assert.Equal("月底结清", credit.GetProperty("remark").GetString());
    }
}
