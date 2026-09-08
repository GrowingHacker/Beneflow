using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 供应商管理单元测试：列表统计（累计采购/最近供货）、重名校验、更新、删除保护。
/// </summary>
public class SupplierServiceTests : TestBase
{
    [Fact]
    public async Task ListAsync_NoPurchase_StatsAreZero()
    {
        var page = await SupplierSvc.ListAsync(null, 1, 20);
        Assert.Equal(1, page.Total);

        var row = page.List[0];
        Assert.Equal(0m, Prop<decimal>(row, "totalAmount"));
        Assert.False(Prop<bool>(row, "hasPurchase"));
        Assert.Null(Prop<string?>(row, "lastDate"));
        Assert.Equal("启用", Prop<string>(row, "status"));
    }

    [Fact]
    public async Task ListAsync_WithPurchase_AggregatesAmount()
    {
        await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = SupplierId,
            Details = new List<PurchaseDetailDto> { new() { ProductId = ProductAId, Qty = 5, CostPrice = 10 } },
        });

        var page = await SupplierSvc.ListAsync(null, 1, 20);
        var row = page.List[0];

        Assert.Equal(50.00m, Prop<decimal>(row, "totalAmount"));
        Assert.True(Prop<bool>(row, "hasPurchase"));
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), Prop<string>(row, "lastDate"));
    }

    [Fact]
    public async Task ListAsync_KeywordFilter()
    {
        await SupplierSvc.CreateAsync(new SupplierUpsertDto { Name = "蒙牛乳业", Contact = "王五" });
        var page = await SupplierSvc.ListAsync("蒙牛", 1, 20);
        Assert.Equal(1, page.Total);
        Assert.Equal("蒙牛乳业", Prop<string>(page.List[0], "name"));
    }

    [Fact]
    public async Task CreateAsync_Success()
    {
        var r = await SupplierSvc.CreateAsync(new SupplierUpsertDto
        {
            Name = "伊利股份", Contact = "赵六", Phone = "13900139000",
            Address = "内蒙古", Remark = "奶制品", StatusRaw = true,
        });
        Assert.Equal(0, r.Code);

        var id = GetResultDataProp<int>(r.Data!, "id");
        Assert.True(await Db.Suppliers.AnyAsync(s => s.Id == id && s.Name == "伊利股份"));
    }

    [Fact]
    public async Task CreateAsync_EmptyName_ReturnsError()
    {
        var r = await SupplierSvc.CreateAsync(new SupplierUpsertDto { Name = "  " });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("请填写供应商名称", r.Message);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_ReturnsError()
    {
        var r = await SupplierSvc.CreateAsync(new SupplierUpsertDto { Name = "测试供应商" });
        Assert.NotEqual(0, r.Code);
        Assert.Contains("供应商名称已存在", r.Message);
    }

    [Fact]
    public async Task GetAsync_ReturnsDetail()
    {
        var r = await SupplierSvc.GetAsync(SupplierId);
        Assert.Equal(0, r.Code);
        Assert.Equal("测试供应商", Prop<string>(r.Data, "name"));
        Assert.Equal("启用", Prop<string>(r.Data, "status"));
    }

    [Fact]
    public async Task GetAsync_NotExist_ReturnsError()
    {
        var r = await SupplierSvc.GetAsync(9999);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("供应商不存在", r.Message);
    }

    [Fact]
    public async Task UpdateAsync_StatusString_Toggles()
    {
        var r = await SupplierSvc.UpdateAsync(SupplierId, Json("{\"status\":\"停用\"}"));
        Assert.Equal(0, r.Code);
        Assert.False((await Db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == SupplierId)).Status);

        await SupplierSvc.UpdateAsync(SupplierId, Json("{\"status\":true}"));
        Assert.True((await Db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == SupplierId)).Status);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesContactFields()
    {
        var r = await SupplierSvc.UpdateAsync(SupplierId,
            Json("{\"contact\":\"孙七\",\"phone\":\"13700137000\",\"address\":\"广州\",\"remark\":\"长期合作\"}"));
        Assert.Equal(0, r.Code);

        var s = await Db.Suppliers.AsNoTracking().FirstAsync(x => x.Id == SupplierId);
        Assert.Equal("孙七", s.Contact);
        Assert.Equal("13700137000", s.Phone);
        Assert.Equal("长期合作", s.Remark);
    }

    [Fact]
    public async Task UpdateAsync_DuplicateName_ReturnsError()
    {
        var other = await SupplierSvc.CreateAsync(new SupplierUpsertDto { Name = "另一家" });
        var otherId = GetResultDataProp<int>(other.Data!, "id");

        var r = await SupplierSvc.UpdateAsync(otherId, Json("{\"name\":\"测试供应商\"}"));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("供应商名称已存在", r.Message);
    }

    [Fact]
    public async Task UpdateAsync_NotExist_ReturnsError()
    {
        var r = await SupplierSvc.UpdateAsync(9999, Json("{\"contact\":\"x\"}"));
        Assert.NotEqual(0, r.Code);
    }

    [Fact]
    public async Task DeleteAsync_WithoutPurchase_Removed()
    {
        var r = await SupplierSvc.DeleteAsync(SupplierId);
        Assert.Equal(0, r.Code);
        Assert.False(await Db.Suppliers.AnyAsync(s => s.Id == SupplierId));
    }

    [Fact]
    public async Task DeleteAsync_WithPurchase_ReturnsError()
    {
        await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = SupplierId,
            Details = new List<PurchaseDetailDto> { new() { ProductId = ProductAId, Qty = 2, CostPrice = 3 } },
        });

        var r = await SupplierSvc.DeleteAsync(SupplierId);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("已有采购记录", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_NotExist_ReturnsError()
    {
        var r = await SupplierSvc.DeleteAsync(9999);
        Assert.NotEqual(0, r.Code);
    }
}
