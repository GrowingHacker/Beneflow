using System.Text.Json;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 上线前集成测试共用的「铺数据」动作（建商品 / 建供应商 / 进货 / 收银）。
///
/// 为什么单独抽一层：这一轮要按接口清单铺开覆盖，多个测试类都需要
/// 「先把一件商品真买进来、再真卖出去」这个前置。放进 <see cref="IntegrationTestBase"/>
/// 会让基类长出领域知识（它只该管认证 / 请求 / 响应包络三件事），所以另起一个中间基类。
///
/// ⚠️ 这里只负责「把数据造成」，**不做契约断言** —— 断言必须留在各测试文件里，
/// 否则断言搬家之后就看不出「哪条用例守的是哪个契约」。
///
/// ⚠️ 全部走真实 HTTP 端点，不直接写 DbContext：铺数据这条路本身也在被测范围里，
/// 直接用 EF 塞数据会绕过「控制器 → Service」的绑定与校验，掩盖真实失败。
/// </summary>
public abstract class IntegrationSeedBase : IntegrationTestBase
{
    protected IntegrationSeedBase(TestWebAppFactory factory) : base(factory) { }

    /// <summary>生成一个不会与种子数据/其他用例冲突的名字</summary>
    protected static string NewTag(string prefix = "IT") => prefix + "_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>取第一个可用分类 id（种子数据保证分类非空）</summary>
    protected async Task<int> FirstCategoryIdAsync()
    {
        var body = await ReadBody(await Client.GetAsync("/api/v1/categories"));
        return body.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
    }

    /// <summary>建商品（库存为 0 时靠进货入库），返回商品 id</summary>
    protected async Task<int> SeedProductAsync(string name, decimal salePrice = 10m, decimal costPrice = 6m,
        decimal stock = 0, decimal safetyStock = 0, bool hasExpiry = false, int shelfLifeDays = 0,
        bool isWeighted = false)
    {
        var catId = await FirstCategoryIdAsync();
        var resp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "",
            name,
            categoryId = catId,
            unit = isWeighted ? "斤" : "瓶",
            salePrice,
            costPrice,
            stockQuantity = stock,
            safetyStock,
            hasExpiry,
            shelfLifeDays,
            isWeighted,
            status = true,
        });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    /// <summary>建供应商，返回 id</summary>
    protected async Task<int> SeedSupplierAsync(string name)
    {
        var resp = await PostJsonAsync("/api/v1/suppliers", new { name, status = true });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    /// <summary>进货入库，返回 <c>{ id, orderNo }</c></summary>
    protected async Task<JsonElement> SeedPurchaseAsync(int supplierId,
        params (int ProductId, decimal Qty, decimal Cost)[] lines)
    {
        var resp = await PostJsonAsync("/api/v1/purchases", new
        {
            supplierId,
            details = lines.Select(l => new { productId = l.ProductId, qty = l.Qty, costPrice = l.Cost }).ToArray(),
            totalQty = lines.Sum(l => l.Qty),
            totalAmount = Math.Round(lines.Sum(l => l.Qty * l.Cost), 2),
        });
        return await ExpectOk(resp);
    }

    /// <summary>
    /// 带生产日期的进货：有效期商品的批次 = 生产日期 +（商品档案的）保质期天数。
    /// 临期/过期用例靠它精确造出「还剩几天到期」，而不必依赖种子数据的固定日期。
    /// </summary>
    protected async Task<JsonElement> SeedPurchaseWithProduceAsync(int supplierId, DateTime produceDate,
        params (int ProductId, decimal Qty, decimal Cost)[] lines)
    {
        var resp = await PostJsonAsync("/api/v1/purchases", new
        {
            supplierId,
            details = lines.Select(l => new
            {
                productId = l.ProductId,
                qty = l.Qty,
                costPrice = l.Cost,
                produceDate = produceDate.ToString("yyyy-MM-dd"),
            }).ToArray(),
            totalQty = lines.Sum(l => l.Qty),
            totalAmount = Math.Round(lines.Sum(l => l.Qty * l.Cost), 2),
        });
        return await ExpectOk(resp);
    }

    /// <summary>现金收银，返回 <c>{ id, orderNo }</c></summary>
    protected async Task<JsonElement> SeedSaleAsync(params (int ProductId, decimal Qty, decimal UnitPrice)[] lines)
    {
        var total = Math.Round(lines.Sum(l => l.Qty * l.UnitPrice), 2);
        var resp = await PostJsonAsync("/api/v1/sales", new
        {
            items = lines.Select(l => new
            {
                productId = l.ProductId,
                qty = l.Qty,
                unitPrice = l.UnitPrice,
                subTotal = Math.Round(l.Qty * l.UnitPrice, 2),
            }).ToArray(),
            totalAmount = total,
            discountAmount = 0,
            payAmount = total,
            payMethod = "现金",
            cashAmount = total,
            changeAmount = 0,
            isCredit = false,
        });
        return await ExpectOk(resp);
    }

    /// <summary>读某商品当前库存数量（商品列表按关键词查，取第一条）</summary>
    protected async Task<decimal> StockOfAsync(string keyword)
    {
        var body = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + Uri.EscapeDataString(keyword)));
        var rows = body.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        return rows.Count == 0 ? -1 : rows[0].GetProperty("stockQuantity").GetDecimal();
    }

    /// <summary>取商品列表里的行（断言行级字段用）</summary>
    protected async Task<List<JsonElement>> ProductRowsAsync(string keyword)
    {
        var body = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + Uri.EscapeDataString(keyword)));
        return body.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
    }

    /// <summary>把响应体读成 (code, message, data) 三元组，便于断言业务失败</summary>
    protected static (int Code, string? Message, JsonElement Data) Unwrap(JsonElement body) =>
        (body.GetProperty("code").GetInt32(),
         body.TryGetProperty("message", out var m) ? m.GetString() : null,
         body.GetProperty("data"));
}
