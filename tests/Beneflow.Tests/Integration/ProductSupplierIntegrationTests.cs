using System.Text.Json;
using Xunit;

namespace Beneflow.Tests.Integration;

/// <summary>
/// 商品与供应商管理集成测试：验证 CRUD 经真实 HTTP 管线落地到数据库、列表/软删除行为正确。
/// </summary>
public class ProductSupplierIntegrationTests : IntegrationTestBase
{
    public ProductSupplierIntegrationTests(TestWebAppFactory factory) : base(factory) { }

    private static async Task<int> FirstCategoryIdAsync(HttpClient client)
    {
        var body = await ReadBody(await client.GetAsync("/api/v1/categories"));
        return body.GetProperty("data").EnumerateArray().First().GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task CreateProduct_ThenAppearsInList()
    {
        await LoginAsAdminAsync();
        var catId = await FirstCategoryIdAsync(Client);
        var name = "ITPROD_" + Guid.NewGuid().ToString("N")[..8];

        var createResp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "",
            name,
            categoryId = catId,
            unit = "瓶",
            salePrice = 8.00,
            costPrice = 5.00,
            stockQuantity = 10,
            safetyStock = 5,
            hasExpiry = false,
            shelfLifeDays = 0,
            isWeighted = false,
            status = true,
            remark = "IT",
        });
        var created = await ExpectOk(createResp);
        var newId = created.GetProperty("id").GetInt32();
        Assert.True(newId > 0);

        var listBody = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.Contains(items, x => x.GetProperty("id").GetInt32() == newId);
    }

    [Fact]
    public async Task SoftDeleteProduct_RemovedFromList()
    {
        await LoginAsAdminAsync();
        var catId = await FirstCategoryIdAsync(Client);
        var name = "ITDEL_" + Guid.NewGuid().ToString("N")[..8];

        var createResp = await PostJsonAsync("/api/v1/products", new
        {
            barcode = "", name, categoryId = catId, unit = "个", salePrice = 1, costPrice = 1,
            stockQuantity = 0, safetyStock = 0, hasExpiry = false, shelfLifeDays = 0, isWeighted = false, status = true,
        });
        var newId = (await ExpectOk(createResp)).GetProperty("id").GetInt32();

        var delResp = await Client.DeleteAsync($"/api/v1/products/{newId}");
        Assert.Equal(0, (await ReadBody(delResp)).GetProperty("code").GetInt32());

        var listBody = await ReadBody(await Client.GetAsync("/api/v1/products?keyword=" + name));
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.DoesNotContain(items, x => x.GetProperty("id").GetInt32() == newId);
    }

    [Fact]
    public async Task CreateSupplier_ThenListShowsIt()
    {
        await LoginAsAdminAsync();
        var name = "ITSUP_" + Guid.NewGuid().ToString("N")[..8];

        var createResp = await PostJsonAsync("/api/v1/suppliers", new
        {
            name,
            contact = "王五",
            phone = "13700001111",
            address = "测试路1号",
            status = true,
        });
        var newId = (await ExpectOk(createResp)).GetProperty("id").GetInt32();

        var listBody = await ReadBody(await Client.GetAsync("/api/v1/suppliers"));
        var items = listBody.GetProperty("data").GetProperty("list").EnumerateArray().ToList();
        Assert.Contains(items, x => x.GetProperty("id").GetInt32() == newId && x.GetProperty("name").GetString() == name);
    }
}
