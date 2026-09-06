using Beneflow.Api.Data;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 测试基类：每个测试用例创建一个独立的 InMemory 数据库，保证测试之间互不影响。
/// </summary>
public abstract class TestBase : IDisposable
{
    protected AppDbContext Db { get; private set; }
    protected FakeCurrentUser CurrentUser { get; private set; }
    protected PurchaseService PurchaseSvc { get; private set; }

    protected TestBase()
    {
        var dbName = $"BeneflowTest_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        CurrentUser = new FakeCurrentUser { Id = 1, Username = "testuser", ClientIp = "127.0.0.1" };
        PurchaseSvc = new PurchaseService(Db, CurrentUser);

        SeedBaseData();
    }

    /// <summary>种子数据：一个供应商、两个商品（一个有有效期，一个没有）</summary>
    private void SeedBaseData()
    {
        var supplier = new Supplier { Name = "测试供应商", Contact = "张三", Phone = "13800138000" };
        Db.Suppliers.Add(supplier);

        var category = new ProductCategory { Name = "测试分类", Sort = 1 };
        Db.Categories.Add(category);
        Db.SaveChanges();

        Db.Products.Add(new Product
        {
            Name = "商品A（无有效期）",
            Barcode = "A001",
            CategoryId = category.Id,
            Unit = "瓶",
            SalePrice = 10.00m,
            CostPrice = 0,
            StockQuantity = 0,
            HasExpiry = false,
            ShelfLifeDays = 0,
            IsDeleted = false,
            CreatedAt = DateTime.Now,
        });

        Db.Products.Add(new Product
        {
            Name = "商品B（有有效期）",
            Barcode = "B001",
            CategoryId = category.Id,
            Unit = "盒",
            SalePrice = 20.00m,
            CostPrice = 0,
            StockQuantity = 0,
            HasExpiry = true,
            ShelfLifeDays = 365,
            IsDeleted = false,
            CreatedAt = DateTime.Now,
        });

        Db.SaveChanges();
    }

    /// <summary>获取指定 ID 的商品最新状态</summary>
    protected Product GetProduct(int id)
    {
        return Db.Products.AsNoTracking().First(p => p.Id == id);
    }

    /// <summary>获取指定 ID 的进货单（含明细）</summary>
    protected PurchaseOrder GetPurchaseOrder(int id)
    {
        return Db.PurchaseOrders
            .AsNoTracking()
            .Include(o => o.Supplier)
            .First(o => o.Id == id);
    }

    /// <summary>获取进货单明细</summary>
    protected List<PurchaseOrderDetail> GetPurchaseDetails(int orderId)
    {
        return Db.PurchaseOrderDetails
            .AsNoTracking()
            .Where(d => d.OrderId == orderId)
            .ToList();
    }

    /// <summary>获取某商品的最后一条库存流水</summary>
    protected StockLog? LastStockLog(int productId)
    {
        return Db.StockLogs.AsNoTracking()
            .Where(s => s.ProductId == productId)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();
    }

    /// <summary>从 ApiResult 的 Data（匿名类型）中提取属性值</summary>
    protected T? GetResultDataProp<T>(object data, string propName)
    {
        var prop = data.GetType().GetProperty(propName);
        if (prop == null) return default;
        return (T?)prop.GetValue(data);
    }

    public void Dispose()
    {
        Db.Dispose();
    }
}

/// <summary>测试用的假用户上下文</summary>
public class FakeCurrentUser : ICurrentUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string? ClientIp { get; set; }
}
