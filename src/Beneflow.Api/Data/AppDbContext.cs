using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Data;

/// <summary>
/// 百惠通数据库上下文（Code First，通过 dotnet ef migrations 建表）。
///
/// 实体的表名、列长度、精度、索引、外键删除行为等 schema 配置，
/// 一律放在 <c>Data/Configurations/</c> 下「一实体一文件」的
/// <see cref="IEntityTypeConfiguration{TEntity}"/> 实现里，由本类统一装配。
///
/// 新增实体时请同步新增对应的 Configuration 文件——即使该实体暂时无需配置也要建，
/// 以保持「实体 ↔ 配置」一一对应，否则漏配了没人会发现。
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserInfo> Users => Set<UserInfo>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Menu> Menus => Set<Menu>();
    public DbSet<RoleMenu> RoleMenus => Set<RoleMenu>();
    public DbSet<ProductCategory> Categories => Set<ProductCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductBatch> Batches => Set<ProductBatch>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderDetail> PurchaseOrderDetails => Set<PurchaseOrderDetail>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnDetail> PurchaseReturnDetails => Set<PurchaseReturnDetail>();
    public DbSet<SaleOrder> SaleOrders => Set<SaleOrder>();
    public DbSet<SaleOrderDetail> SaleOrderDetails => Set<SaleOrderDetail>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<SaleReturnDetail> SaleReturnDetails => Set<SaleReturnDetail>();
    public DbSet<CreditSale> CreditSales => Set<CreditSale>();
    public DbSet<CreditPayment> CreditPayments => Set<CreditPayment>();
    public DbSet<StockLog> StockLogs => Set<StockLog>();
    public DbSet<StockCheck> StockChecks => Set<StockCheck>();
    public DbSet<StockCheckDetail> StockCheckDetails => Set<StockCheckDetail>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<LoginLog> LoginLogs => Set<LoginLog>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        // 从当前程序集扫描全部 IEntityTypeConfiguration<> 实现并批量装配。
        // 好处：新增实体只需加一个 Configuration 文件，本类无需再改。
        mb.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
