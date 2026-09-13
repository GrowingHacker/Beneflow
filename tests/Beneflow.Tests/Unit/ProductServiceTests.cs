using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 商品档案与分类单元测试：新增（条码/价格/自动分类）、局部更新、软删除、分类 CRUD。
/// </summary>
public class ProductServiceTests : TestBase
{
    private static ProductUpsertDto NewProduct(string name, string barcode, decimal salePrice = 9.9m) =>
        new()
        {
            Barcode = barcode, Name = name, CategoryId = 0, CategoryName = "测试分类",
            Unit = "瓶", SalePrice = salePrice, CostPrice = 5, StatusRaw = true,
        };

    // ================= 新增商品 =================

    [Fact]
    public async Task CreateAsync_Success_WritesLog()
    {
        var r = await ProductSvc.CreateAsync(NewProduct("可口可乐", "C001"));
        Assert.Equal(0, r.Code);

        var id = GetResultDataProp<int>(r.Data!, "id");
        var p = GetProduct(id);
        Assert.Equal("C001", p.Barcode);
        Assert.Equal(CategoryId, p.CategoryId);
        Assert.False(p.IsDeleted);

        var log = await Db.OperationLogs.AsNoTracking()
            .OrderByDescending(l => l.Id).FirstAsync(l => l.Module == "商品管理");
        Assert.Equal("新增商品", log.Action);
    }

    [Fact]
    public async Task CreateAsync_DuplicateBarcode_ReturnsError()
    {
        await ProductSvc.CreateAsync(NewProduct("商品1", "DUP001"));
        var dup = await ProductSvc.CreateAsync(NewProduct("商品2", "DUP001"));

        Assert.NotEqual(0, dup.Code);
        Assert.Contains("已存在", dup.Message);
    }

    [Fact]
    public async Task CreateAsync_EmptyBarcode_GeneratesInStoreCode()
    {
        var r = await ProductSvc.CreateAsync(NewProduct("散装花生", ""));
        Assert.Equal(0, r.Code);
        var barcode = GetResultDataProp<string>(r.Data!, "barcode")!;
        Assert.StartsWith("L", barcode);
        Assert.True(barcode.Length > 10);
    }

    [Fact]
    public async Task CreateAsync_NewCategoryName_AutoCreated()
    {
        var dto = NewProduct("进口零食", "N001");
        dto.CategoryId = 0;
        dto.CategoryName = "进口食品";

        var r = await ProductSvc.CreateAsync(dto);
        Assert.Equal(0, r.Code);
        Assert.True(await Db.Categories.AnyAsync(c => c.Name == "进口食品"));
    }

    [Fact]
    public async Task CreateAsync_UnknownCategoryId_ReturnsError()
    {
        var dto = NewProduct("无分类商品", "X001");
        dto.CategoryId = 9999;
        dto.CategoryName = null;

        var r = await ProductSvc.CreateAsync(dto);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("分类不存在", r.Message);
    }

    [Fact]
    public async Task CreateAsync_NegativePrice_ReturnsError()
    {
        var r = await ProductSvc.CreateAsync(NewProduct("负价商品", "P001", -1));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("价格不能为负", r.Message);
    }

    [Fact]
    public async Task CreateAsync_InitialStock_WritesStockLogAndBatch()
    {
        var dto = NewProduct("酸奶", "Y001");
        dto.StockQuantity = 12;
        dto.HasExpiry = true;
        dto.ShelfLifeDays = 30;

        var r = await ProductSvc.CreateAsync(dto);
        var id = GetResultDataProp<int>(r.Data!, "id");

        Assert.Equal(12, GetProduct(id).StockQuantity);
        var log = LastStockLog(id)!;
        Assert.Equal("期初建账", log.ChangeType);
        Assert.Equal(12, log.ChangeQty);
        Assert.Equal("INIT", log.RefNo);

        var batch = await Db.Batches.AsNoTracking().FirstAsync(b => b.ProductId == id);
        Assert.Equal(12, batch.Quantity);
        Assert.Equal(DateTime.Today.AddDays(30), batch.ExpireDate);
    }

    // ================= 查询 =================

    [Fact]
    public async Task GetByBarcode_Existing_ReturnsProduct()
    {
        var r = await ProductSvc.GetByBarcode("A001");
        Assert.Equal(0, r.Code);
        Assert.Equal("商品A（无有效期）", Prop<string>(r.Data, "Name"));
        Assert.Equal("上架", Prop<string>(r.Data, "Status"));
        Assert.Equal("A001", Prop<string>(r.Data, "Barcode"));
        Assert.Equal("测试分类", Prop<string>(r.Data, "CategoryName"));
    }

    [Fact]
    public async Task GetByBarcode_NotExisting_ReturnsError()
    {
        var r = await ProductSvc.GetByBarcode("NOT-EXIST");
        Assert.NotEqual(0, r.Code);
        Assert.Contains("商品不存在", r.Message);
    }

    [Fact]
    public async Task ListAsync_FilterByKeyword()
    {
        var page = await ProductSvc.ListAsync("A001", 1, 20);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task ListAsync_ExcludesDeleted()
    {
        await ProductSvc.DeleteAsync(ProductAId);
        var page = await ProductSvc.ListAsync(null, 1, 20);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task GetWeightedProductsAsync_OnlyOnSaleWeighted()
    {
        var dto = NewProduct("散装大米", "W001");
        dto.IsWeighted = true;
        await ProductSvc.CreateAsync(dto);

        var list = await ProductSvc.GetWeightedProductsAsync();
        Assert.Single(list);
        Assert.Equal("散装大米", Prop<string>(list[0], "Name"));
    }

    // ================= 编辑商品 =================

    [Fact]
    public async Task UpdateAsync_PartialFields_OnlyChangedApplied()
    {
        var r = await ProductSvc.UpdateAsync(ProductAId, Json("{\"name\":\"商品A改名\",\"salePrice\":15.5}"));
        Assert.Equal(0, r.Code);

        var p = GetProduct(ProductAId);
        Assert.Equal("商品A改名", p.Name);
        Assert.Equal(15.5m, p.SalePrice);
        Assert.Equal("瓶", p.Unit);            // 未提交的字段保持原值
        Assert.False(string.IsNullOrEmpty(p.PinyinCode));   // 改名后拼音码重新生成
    }

    [Fact]
    public async Task UpdateAsync_StatusString_ConvertedToBool()
    {
        var r = await ProductSvc.UpdateAsync(ProductAId, Json("{\"status\":\"下架\"}"));
        Assert.Equal(0, r.Code);
        Assert.False(GetProduct(ProductAId).Status);

        await ProductSvc.UpdateAsync(ProductAId, Json("{\"status\":\"上架\"}"));
        Assert.True(GetProduct(ProductAId).Status);
    }

    [Fact]
    public async Task UpdateAsync_DuplicateBarcode_ReturnsError()
    {
        var r = await ProductSvc.UpdateAsync(ProductAId, Json("{\"barcode\":\"B001\"}"));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("已被其他商品使用", r.Message);
    }

    [Fact]
    public async Task UpdateAsync_NegativePrice_ReturnsError()
    {
        var r = await ProductSvc.UpdateAsync(ProductAId, Json("{\"salePrice\":-3}"));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("价格不能为负", r.Message);
    }

    [Fact]
    public async Task UpdateAsync_NotExist_ReturnsError()
    {
        var r = await ProductSvc.UpdateAsync(9999, Json("{\"name\":\"不存在\"}"));
        Assert.NotEqual(0, r.Code);
        Assert.Contains("商品不存在", r.Message);
    }

    [Fact]
    public async Task UpdateAsync_NoFieldChanged_NoOperationLog()
    {
        await Logs.WriteAsync("商品管理", "前置日志", "用于确认无变更时不新增日志");
        await Db.SaveChangesAsync();
        var before = await Db.OperationLogs.CountAsync(l => l.Module == "商品管理" && l.Action == "编辑商品");

        var r = await ProductSvc.UpdateAsync(ProductAId, Json("{\"name\":\"商品A（无有效期）\"}"));   // 与原名一致
        Assert.Equal(0, r.Code);

        var after = await Db.OperationLogs.CountAsync(l => l.Module == "商品管理" && l.Action == "编辑商品");
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task UpdateAsync_CategoryName_CreatesCategory()
    {
        var r = await ProductSvc.UpdateAsync(ProductAId, Json("{\"categoryId\":0,\"categoryName\":\"饮料\"}"));
        Assert.Equal(0, r.Code);
        Assert.True(await Db.Categories.AnyAsync(c => c.Name == "饮料"));
        Assert.NotEqual(CategoryId, GetProduct(ProductAId).CategoryId);
    }

    // ================= 删除商品 =================

    [Fact]
    public async Task DeleteAsync_SoftDelete()
    {
        var r = await ProductSvc.DeleteAsync(ProductAId);
        Assert.Equal(0, r.Code);

        var p = GetProduct(ProductAId);
        Assert.True(p.IsDeleted);
        Assert.False(p.Status);          // 删除同时下架
    }

    [Fact]
    public async Task DeleteAsync_NotExist_ReturnsError()
    {
        var r = await ProductSvc.DeleteAsync(9999);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("商品不存在", r.Message);
    }

    [Fact]
    public async Task DeleteAsync_AlreadyDeleted_ReturnsError()
    {
        await ProductSvc.DeleteAsync(ProductAId);
        var again = await ProductSvc.DeleteAsync(ProductAId);
        Assert.NotEqual(0, again.Code);
    }

    // ================= 分类 =================

    [Fact]
    public async Task CreateCategoryAsync_DuplicateName_ReturnsError()
    {
        await ProductSvc.CreateCategoryAsync("酒水", null);
        var dup = await ProductSvc.CreateCategoryAsync("酒水", null);

        Assert.NotEqual(0, dup.Code);
        Assert.Contains("分类名称已存在", dup.Message);
    }

    [Fact]
    public async Task CreateCategoryAsync_EmptyName_ReturnsError()
    {
        var r = await ProductSvc.CreateCategoryAsync("   ", null);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("请填写分类名称", r.Message);
    }

    [Fact]
    public async Task CategoriesAsync_OrderedBySort()
    {
        await ProductSvc.CreateCategoryAsync("零食", null);
        var list = await ProductSvc.CategoriesAsync();
        Assert.True(list.Count >= 2);
        // 按 Sort 升序、同 Sort 按 Id 升序
        var sorts = list.Select(c => c.Sort).ToList();
        Assert.Equal(sorts.OrderBy(s => s).ToList(), sorts);
    }

    [Fact]
    public async Task DeleteCategoryAsync_WithProducts_ReturnsError()
    {
        var r = await ProductSvc.DeleteCategoryAsync(CategoryId);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("存在商品", r.Message);
    }

    [Fact]
    public async Task DeleteCategoryAsync_WithChildren_ReturnsError()
    {
        var parent = await ProductSvc.CreateCategoryAsync("酒水", null);
        var parentId = GetResultDataProp<int>(parent.Data!, "id");
        var child = new ProductCategory { Name = "啤酒", ParentId = parentId };
        Db.Categories.Add(child);
        await Db.SaveChangesAsync();

        var r = await ProductSvc.DeleteCategoryAsync(parentId);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("子分类", r.Message);
    }

    [Fact]
    public async Task DeleteCategoryAsync_EmptyCategory_Removed()
    {
        var created = await ProductSvc.CreateCategoryAsync("空分类", null);
        var id = GetResultDataProp<int>(created.Data!, "id");

        var r = await ProductSvc.DeleteCategoryAsync(id);
        Assert.Equal(0, r.Code);
        Assert.False(await Db.Categories.AnyAsync(c => c.Id == id));
    }

    [Fact]
    public async Task DeleteCategoryAsync_NotExist_ReturnsError()
    {
        var r = await ProductSvc.DeleteCategoryAsync(9999);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("分类不存在", r.Message);
    }
}
