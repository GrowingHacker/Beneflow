using System.Text.Json;
using Beneflow.Api.Data;
using Beneflow.Api.Models.Entities;
using Beneflow.Api.Services;
using Beneflow.Api.Utils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Beneflow.Tests;

/// <summary>
/// 测试基类：每个测试用例创建一个独立的 InMemory 数据库，保证测试之间互不影响。
/// 种子数据覆盖：1 个用户（店主角色）+ 2 个角色 + 1 个供应商 + 1 个分类 + 2 个商品（一个有有效期，一个没有）。
/// 所有业务服务都在这里直接实例化（不走 DI），保持单测轻量。
/// </summary>
public abstract class TestBase : IDisposable
{
    protected AppDbContext Db { get; }
    protected FakeCurrentUser CurrentUser { get; }
    protected IConfiguration Config { get; }
    protected ILogService Logs { get; }

    // ========== 业务服务 ==========
    protected PurchaseService PurchaseSvc { get; }
    protected SaleService SaleSvc { get; }
    protected StockService StockSvc { get; }
    protected ProductService ProductSvc { get; }
    protected ReportService ReportSvc { get; }
    protected UserService UserSvc { get; }
    protected RoleService RoleSvc { get; }
    protected MenuService MenuSvc { get; }
    protected SupplierService SupplierSvc { get; }
    protected AuthService AuthSvc { get; }
    protected LogService LogSvc { get; }
    protected SettingService SettingSvc { get; }

    // ========== 种子数据主键 ==========
    protected int UserId { get; private set; }
    protected int SupplierId { get; private set; }
    protected int CategoryId { get; private set; }
    /// <summary>商品A：无有效期，售价 10，初始库存 0</summary>
    protected int ProductAId { get; private set; }
    /// <summary>商品B：有有效期（保质期 365 天），售价 20，初始库存 0</summary>
    protected int ProductBId { get; private set; }
    protected int OwnerRoleId { get; private set; }
    protected int CashierRoleId { get; private set; }

    protected TestBase()
    {
        var dbName = $"BeneflowTest_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        Config = BuildConfig();
        CurrentUser = new FakeCurrentUser { Id = 1, Username = "testuser", ClientIp = "127.0.0.1" };
        Logs = new LogService(Db, CurrentUser);

        PurchaseSvc = new PurchaseService(Db, CurrentUser);
        SaleSvc = new SaleService(Db, CurrentUser);
        StockSvc = new StockService(Db, CurrentUser);
        ProductSvc = new ProductService(Db, CurrentUser, Logs);
        ReportSvc = new ReportService(Db);
        UserSvc = new UserService(Db, CurrentUser, Logs, new AccountStatusCache());
        RoleSvc = new RoleService(Db, Logs);
        MenuSvc = new MenuService(Db, Logs);
        SupplierSvc = new SupplierService(Db);
        LogSvc = new LogService(Db, CurrentUser);
        AuthSvc = new AuthService(Db, Config, NullLogger<AuthService>.Instance);
        SettingSvc = new SettingService(Db, new AesStringCipher(BuildConfig(), NewHostEnv()), Logs, Config, NewHostEnv());

        SeedBaseData();
    }

    /// <summary>测试用配置：JWT、AES 密钥（避免首次使用时写 appsettings.json）、空连接串</summary>
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = "beneflow-unit-test-secret-key-0123456789",
            ["Jwt:Issuer"] = "Beneflow.Test",
            ["Jwt:Audience"] = "Beneflow.Test",
            ["Jwt:ExpireMinutes"] = "720",
            ["Security:AesKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
            ["ConnectionStrings:Default"] = "Server=(local);Database=BeneflowTest;Trusted_Connection=True;",
        }).Build();

    /// <summary>独立的加解密实例（密钥来自测试配置，不落盘）</summary>
    protected static AesStringCipher NewCipher() => new(BuildConfig(), NewHostEnv());

    /// <summary>假宿主环境：内容目录指向临时目录，避免测试污染仓库</summary>
    protected static FakeHostEnv NewHostEnv() =>
        new(Path.Combine(Path.GetTempPath(), "beneflow-test", Guid.NewGuid().ToString("N")));

    // ========== 种子数据 ==========

    /// <summary>种子数据：两个角色、一个店主用户、一个供应商、一个分类、两个商品</summary>
    private void SeedBaseData()
    {
        // 角色（先建角色，供用户关联）
        var owner = new Role { Name = "店主", Code = "owner", Description = "系统内置超管" };
        var cashier = new Role { Name = "收银员", Code = "cashier" };
        Db.Roles.AddRange(owner, cashier);
        Db.SaveChanges();
        OwnerRoleId = owner.Id;
        CashierRoleId = cashier.Id;

        // 用户：销售/库存/进货列表等查询都要 join Users，必须先有一条
        var user = new UserInfo
        {
            Username = "admin", Name = "测试店主",
            Salt = PasswordHasher.NewSalt(),
            Status = true,
        };
        user.PasswordHash = PasswordHasher.Hash("123456", user.Salt);
        Db.Users.Add(user);
        Db.SaveChanges();
        Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = owner.Id });
        Db.SaveChanges();

        UserId = user.Id;
        CurrentUser.Id = user.Id;
        CurrentUser.Username = user.Username;

        var supplier = new Supplier { Name = "测试供应商", Contact = "张三", Phone = "13800138000" };
        Db.Suppliers.Add(supplier);

        var category = new ProductCategory { Name = "测试分类", Sort = 1 };
        Db.Categories.Add(category);
        Db.SaveChanges();

        SupplierId = supplier.Id;
        CategoryId = category.Id;

        var pa = new Product
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
        };

        var pb = new Product
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
        };

        Db.Products.AddRange(pa, pb);
        Db.SaveChanges();

        ProductAId = pa.Id;
        ProductBId = pb.Id;
    }

    // ========== 常用断言辅助 ==========

    /// <summary>获取指定 ID 的商品最新状态</summary>
    protected Product GetProduct(int id) => Db.Products.AsNoTracking().First(p => p.Id == id);

    /// <summary>获取指定 ID 的进货单（含供应商）</summary>
    protected PurchaseOrder GetPurchaseOrder(int id) =>
        Db.PurchaseOrders.AsNoTracking().Include(o => o.Supplier).First(o => o.Id == id);

    /// <summary>获取进货单明细</summary>
    protected List<PurchaseOrderDetail> GetPurchaseDetails(int orderId) =>
        Db.PurchaseOrderDetails.AsNoTracking().Where(d => d.OrderId == orderId).ToList();

    /// <summary>获取某商品的最后一条库存流水</summary>
    protected StockLog? LastStockLog(int productId) =>
        Db.StockLogs.AsNoTracking()
            .Where(s => s.ProductId == productId)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();

    /// <summary>从 ApiResult 的 Data（匿名类型）中提取属性值</summary>
    protected T? GetResultDataProp<T>(object data, string propName)
    {
        var prop = data.GetType().GetProperty(propName);
        if (prop == null) return default;
        return (T?)prop.GetValue(data);
    }

    /// <summary>读取任意对象（多为匿名类型）的属性值</summary>
    protected static object? Prop(object? obj, string propName) =>
        obj?.GetType().GetProperty(propName)?.GetValue(obj);

    /// <summary>读取任意对象的属性值并转换为指定类型</summary>
    protected static T? Prop<T>(object? obj, string propName)
    {
        var v = Prop(obj, propName);
        return v is T t ? t : default;
    }

    /// <summary>构造 JsonElement（供接收 JsonElement 的服务方法使用）</summary>
    protected static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>直接落库一张销售单（用于报表类测试，避免依赖销售服务）</summary>
    protected SaleOrder SeedSale(decimal payAmount, DateTime createdAt, bool isCredit = false, bool isVoided = false,
        string payMethod = "现金", decimal discount = 0)
    {
        var o = new SaleOrder
        {
            OrderNo = $"SO{createdAt:yyyyMMdd}{Db.SaleOrders.Count() + 1:D3}",
            TotalAmount = payAmount + discount,
            DiscountAmount = discount,
            PayAmount = payAmount,
            PayMethod = payMethod,
            CashAmount = payAmount,
            IsCredit = isCredit,
            IsVoided = isVoided,
            CreatedBy = UserId,
            CreatedAt = createdAt,
        };
        Db.SaleOrders.Add(o);
        Db.SaveChanges();
        return o;
    }

    /// <summary>为销售单补一条明细（成本快照用于毛利核算）</summary>
    protected SaleOrderDetail SeedSaleDetail(SaleOrder order, int productId, decimal qty, decimal unitPrice, decimal costPrice)
    {
        var p = GetProduct(productId);
        var d = new SaleOrderDetail
        {
            OrderId = order.Id, ProductId = productId, ProductName = p.Name, Barcode = p.Barcode,
            Quantity = qty, UnitPrice = unitPrice, CostPrice = costPrice,
            SubTotal = Math.Round(qty * unitPrice, 2),
        };
        Db.SaleOrderDetails.Add(d);
        Db.SaveChanges();
        return d;
    }

    /// <summary>直接设置商品库存/成本，跳过采购流程</summary>
    protected void SetStock(int productId, decimal qty, decimal costPrice = 5.00m)
    {
        var p = Db.Products.First(x => x.Id == productId);
        p.StockQuantity = qty;
        p.CostPrice = costPrice;
        Db.SaveChanges();
    }

    /// <summary>设置系统配置组（JSON 值）</summary>
    protected void SetConfig(string key, string json)
    {
        var row = Db.SystemConfigs.FirstOrDefault(c => c.ConfigKey == key);
        if (row == null) Db.SystemConfigs.Add(new SystemConfig { ConfigKey = key, ConfigValue = json });
        else row.ConfigValue = json;
        Db.SaveChanges();
    }

    public void Dispose() => Db.Dispose();
}

/// <summary>测试用的假用户上下文</summary>
public class FakeCurrentUser : ICurrentUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string? ClientIp { get; set; }
}

/// <summary>测试用的假宿主环境（系统设置 / 加解密需要 IWebHostEnvironment）</summary>
public class FakeHostEnv : IWebHostEnvironment
{
    public FakeHostEnv(string contentRoot)
    {
        ContentRootPath = contentRoot;
        Directory.CreateDirectory(contentRoot);
        ContentRootFileProvider = new NullFileProvider();
        WebRootFileProvider = new NullFileProvider();
    }

    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "Beneflow.Tests";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; }
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
