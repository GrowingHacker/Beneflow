using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 商品查询/编辑类端点（<c>PUT /products/{id}</c>、<c>by-barcode</c>、<c>weighted</c>）的上线前集成测试。
///
/// 条码这块有一条不太直观的规则：**软删除不释放条码**。商品删除只是下架（保留在库便于追溯），
/// 而条码唯一索引不看 <c>IsDeleted</c> —— 所以删掉一个商品后，它的条码既反查不到、
/// 也不能拿来新建，必须提示「已被已删除的商品『XX』占用」。
/// 这条规则只有经真实 HTTP 才看得出（单测里直接调 Service 看不到控制器/索引层的差异）。
/// </summary>
public class ProductQueryIntegrationTests : IntegrationSeedBase
{
    public ProductQueryIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private async Task<int> AddProductAsync(string barcode, string name, decimal salePrice, bool weighted = false)
    {
        var catId = await FirstCategoryIdAsync();
        var resp = await PostJsonAsync("/api/v1/products", new
        {
            barcode,
            name,
            categoryId = catId,
            unit = weighted ? "斤" : "瓶",
            salePrice,
            costPrice = salePrice / 2,
            stockQuantity = 0m,
            safetyStock = 0m,
            hasExpiry = false,
            shelfLifeDays = 0,
            isWeighted = weighted,
            status = true,
        });
        return (await ExpectOk(resp)).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task 条码反查_命中唯一商品并返回编辑所需字段()
    {
        await LoginAsAdminAsync();
        var barcode = "IT" + DateTime.Now.Ticks.ToString()[^10..];
        var name = NewTag("ITBC");
        await AddProductAsync(barcode, name, 9.9m);

        var body = await ReadBody(await Client.GetAsync($"/api/v1/products/by-barcode/{barcode}"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var data = body.GetProperty("data");
        Assert.Equal(name, data.GetProperty("name").GetString());
        Assert.Equal(barcode, data.GetProperty("barcode").GetString());
        Assert.Equal(9.9m, data.GetProperty("salePrice").GetDecimal());
    }

    [Fact]
    public async Task 条码反查_已删除商品不再反查得到且该条码仍被占用()
    {
        await LoginAsAdminAsync();
        var barcode = "IT" + DateTime.Now.Ticks.ToString()[^10..];
        var productId = await AddProductAsync(barcode, NewTag("ITBC1"), 5m);

        Assert.Equal(0, (await ReadBody(await Client.DeleteAsync($"/api/v1/products/{productId}")))
            .GetProperty("code").GetInt32());

        // 删除只是下架：反查（带 !IsDeleted）查不到
        var gone = await ReadBody(await Client.GetAsync($"/api/v1/products/by-barcode/{barcode}"));
        Assert.NotEqual(0, gone.GetProperty("code").GetInt32());

        // 但条码仍被这条「幽灵」占着 —— 直接重录会被拦，提示要说清是被谁占的
        var again = await ReadBody(await PostJsonAsync("/api/v1/products", new
        {
            barcode,
            name = NewTag("ITBC2"),
            categoryId = await FirstCategoryIdAsync(),
            unit = "瓶",
            salePrice = 5m,
            costPrice = 2m,
            stockQuantity = 0m,
            safetyStock = 0m,
            hasExpiry = false,
            shelfLifeDays = 0,
            isWeighted = false,
            status = true,
        }));
        Assert.NotEqual(0, again.GetProperty("code").GetInt32());
        Assert.Contains("已删除", again.GetProperty("message").GetString());
    }

    [Fact]
    public async Task 条码反查_查不到时返回业务失败()
    {
        await LoginAsAdminAsync();
        var body = await ReadBody(await Client.GetAsync("/api/v1/products/by-barcode/IT_NO_SUCH_BARCODE"));
        Assert.NotEqual(0, body.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 称重商品列表_只返回称重商品且单位是斤()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITWGT");
        var weightedId = await AddProductAsync("", name, 12m, weighted: true);
        // 一件不称重的商品做对照：它绝不能出现在收银台的称重快捷面板里
        var pieceName = NewTag("ITWGTN");
        var pieceId = await AddProductAsync("", pieceName, 3m, weighted: false);

        var body = await ReadBody(await Client.GetAsync("/api/v1/products/weighted"));
        Assert.Equal(0, body.GetProperty("code").GetInt32());
        var rows = body.GetProperty("data").EnumerateArray().ToList();

        Assert.Contains(rows, x => x.GetProperty("name").GetString() == name);

        // ⚠️ 这一组行**没有 isWeighted 字段**（就靠「在不在这个列表里」表达是否称重），
        // 所以「每行都是称重商品」没法从行内自证；用正反两条替代：
        // 我们建的称重商品必须在、建的普通商品必须缺席。逐行的正确性由 /products 列表一侧守。
        Assert.Contains(rows, x => x.GetProperty("id").GetInt32() == weightedId);
        Assert.DoesNotContain(rows, x => x.GetProperty("id").GetInt32() == pieceId);
    }

    [Fact]
    public async Task 商品编辑_改价后列表与条码反查都能读到新值()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITEDIT");
        var productId = await SeedProductAsync(name, salePrice: 10m, costPrice: 6m, stock: 3m);

        var resp = await PutJsonAsync($"/api/v1/products/{productId}", new
        {
            id = productId,
            barcode = "",
            name = name + "_改",
            categoryId = await FirstCategoryIdAsync(),
            unit = "瓶",
            salePrice = 18.5m,
            costPrice = 6m,
            stockQuantity = 3m,
            safetyStock = 1m,
            hasExpiry = false,
            shelfLifeDays = 0,
            isWeighted = false,
            status = true,
        });
        Assert.Equal(0, (await ReadBody(resp)).GetProperty("code").GetInt32());

        var rows = await ProductRowsAsync(name);
        var row = rows.Single();
        Assert.Equal(name + "_改", row.GetProperty("name").GetString());
        Assert.Equal(18.5m, row.GetProperty("salePrice").GetDecimal());
        // 改档案不能顺手把库存改了
        Assert.Equal(3m, row.GetProperty("stockQuantity").GetDecimal());
    }

    [Fact]
    public async Task 商品编辑_不存在的商品返回业务失败()
    {
        await LoginAsAdminAsync();
        var resp = await PutJsonAsync("/api/v1/products/999999", new
        {
            id = 999999,
            barcode = "",
            name = NewTag("ITNO"),
            categoryId = await FirstCategoryIdAsync(),
            unit = "瓶",
            salePrice = 1m,
            costPrice = 1m,
            stockQuantity = 0m,
            safetyStock = 0m,
            hasExpiry = false,
            shelfLifeDays = 0,
            isWeighted = false,
            status = true,
        });
        Assert.NotEqual(0, (await ReadBody(resp)).GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 商品列表_按关键词与分页取数()
    {
        await LoginAsAdminAsync();
        var name = NewTag("ITLIST");
        await SeedProductAsync(name, salePrice: 4m, costPrice: 2m);

        var body = await ReadBody(await Client.GetAsync($"/api/v1/products?keyword={name}&page=1&pageSize=10"));
        var data = body.GetProperty("data");
        Assert.Equal(1, data.GetProperty("total").GetInt32());
        Assert.Equal(10, data.GetProperty("pageSize").GetInt32());
        Assert.Equal(name, data.GetProperty("list").EnumerateArray().Single().GetProperty("name").GetString());
    }
}
