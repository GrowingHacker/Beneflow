using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Data;

/// <summary>
/// 百惠通数据库上下文（Code First，通过 dotnet ef migrations 建表）。
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
        // ---------- 用户 ----------
        mb.Entity<UserInfo>(e =>
        {
            e.ToTable("UserInfo");
            e.Property(x => x.Username).HasMaxLength(20);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Salt).HasMaxLength(64);
            e.Property(x => x.PasswordHash).HasMaxLength(256);
        });

        mb.Entity<Role>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.Property(x => x.Code).HasMaxLength(30);
            e.HasIndex(x => x.Code).IsUnique();
        });

        mb.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Menu>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
            e.Property(x => x.PermCode).HasMaxLength(50);
            e.Property(x => x.Type).HasMaxLength(10);
            e.HasIndex(x => x.PermCode);
        });

        mb.Entity<RoleMenu>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.MenuId });
            e.HasOne(x => x.Menu).WithMany().HasForeignKey(x => x.MenuId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- 商品 ----------
        mb.Entity<ProductCategory>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(50);
        });

        mb.Entity<Product>(e =>
        {
            e.Property(x => x.Barcode).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Unit).HasMaxLength(10);
            e.Property(x => x.Spec).HasMaxLength(50);
            e.Property(x => x.ImageUrl).HasMaxLength(200);
            // 条码唯一（允许多个空字符串差异，入库时统一生成店内码）
            e.HasIndex(x => x.Barcode).IsUnique();
            e.Property(x => x.StockQuantity).HasPrecision(10, 3);
            e.Property(x => x.SafetyStock).HasPrecision(10, 3);
            e.Property(x => x.SalePrice).HasPrecision(10, 2);
            e.Property(x => x.CostPrice).HasPrecision(10, 2);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<ProductBatch>(e =>
        {
            e.Property(x => x.BatchNo).HasMaxLength(30);
            e.Property(x => x.Quantity).HasPrecision(10, 3);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<Supplier>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Contact).HasMaxLength(50);
            e.Property(x => x.Phone).HasMaxLength(20);
        });

        // ---------- 单据金额精度统一 ----------
        mb.Entity<PurchaseOrder>(e =>
        {
            e.Property(x => x.OrderNo).HasMaxLength(20);
            e.Property(x => x.TotalQty).HasPrecision(10, 3);
            e.Property(x => x.TotalAmount).HasPrecision(12, 2);
        });
        mb.Entity<PurchaseReturn>(e =>
        {
            e.Property(x => x.OrderNo).HasMaxLength(20);
            e.Property(x => x.RefundAmount).HasPrecision(12, 2);
        });

        mb.Entity<PurchaseOrderDetail>(e =>
        {
            e.Property(x => x.Qty).HasPrecision(10, 3);
            e.Property(x => x.CostPrice).HasPrecision(10, 2);
            e.Property(x => x.SubTotal).HasPrecision(12, 2);
        });
        mb.Entity<PurchaseReturnDetail>(e =>
        {
            e.Property(x => x.Qty).HasPrecision(10, 3);
            e.Property(x => x.CostPrice).HasPrecision(10, 2);
            e.Property(x => x.SubTotal).HasPrecision(12, 2);
        });

        mb.Entity<SaleOrder>(e =>
        {
            e.Property(x => x.OrderNo).HasMaxLength(20);
            e.Property(x => x.TotalAmount).HasPrecision(12, 2);
            e.Property(x => x.DiscountAmount).HasPrecision(12, 2);
            e.Property(x => x.PayAmount).HasPrecision(12, 2);
            e.Property(x => x.CashAmount).HasPrecision(12, 2);
            e.Property(x => x.ChangeAmount).HasPrecision(12, 2);
            e.Property(x => x.PayMethod).HasMaxLength(20);
            e.Property(x => x.WechatId).HasMaxLength(50);
        });
        mb.Entity<SaleOrderDetail>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(10, 3);
            e.Property(x => x.UnitPrice).HasPrecision(10, 2);
            e.Property(x => x.CostPrice).HasPrecision(10, 2);
            e.Property(x => x.SubTotal).HasPrecision(12, 2);
            e.Property(x => x.ReturnedQuantity).HasPrecision(10, 3).HasDefaultValueSql("(0)");
        });
        mb.Entity<SaleReturn>(e => e.Property(x => x.RefundAmount).HasPrecision(12, 2));
        mb.Entity<SaleReturnDetail>(e =>
        {
            e.Property(x => x.Qty).HasPrecision(10, 3);
            e.Property(x => x.UnitPrice).HasPrecision(10, 2);
            e.Property(x => x.SubTotal).HasPrecision(12, 2);
        });

        mb.Entity<CreditSale>(e =>
        {
            e.Property(x => x.CreditAmount).HasPrecision(12, 2);
            e.Property(x => x.PaidAmount).HasPrecision(12, 2);
            e.Property(x => x.RemainingAmount).HasPrecision(12, 2);
        });
        mb.Entity<CreditPayment>(e =>
        {
            e.Property(x => x.PayAmount).HasPrecision(12, 2);
            e.Property(x => x.PayMethod).HasMaxLength(20);
        });

        // ---------- 库存 ----------
        mb.Entity<StockLog>(e =>
        {
            e.Property(x => x.ChangeType).HasMaxLength(20);
            e.Property(x => x.ChangeQty).HasPrecision(10, 3);
            e.Property(x => x.BeforeQty).HasPrecision(10, 3);
            e.Property(x => x.AfterQty).HasPrecision(10, 3);
            e.Property(x => x.RefNo).HasMaxLength(30);
            e.HasIndex(x => new { x.ProductId, x.CreatedAt });
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
             .OnDelete(DeleteBehavior.Restrict);
        });
        mb.Entity<StockCheck>(e =>
        {
            e.Property(x => x.OrderNo).HasMaxLength(20);
            e.Property(x => x.Range).HasMaxLength(50);
            e.Property(x => x.ProfitQty).HasPrecision(10, 3);
            e.Property(x => x.LossQty).HasPrecision(10, 3);
        });
        mb.Entity<StockCheckDetail>(e =>
        {
            e.Property(x => x.BookQty).HasPrecision(10, 3);
            e.Property(x => x.ActualQty).HasPrecision(10, 3);
            e.Property(x => x.DiffQty).HasPrecision(10, 3);
        });
    }
}
