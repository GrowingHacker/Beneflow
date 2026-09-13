using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>商品表配置（移动加权平均成本核算）。</summary>
public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> e)
    {
        e.Property(x => x.Barcode).HasMaxLength(20);
        e.Property(x => x.Name).HasMaxLength(100);
        e.Property(x => x.Unit).HasMaxLength(10);
        e.Property(x => x.Spec).HasMaxLength(50);
        e.Property(x => x.ImageUrl).HasMaxLength(200);
        // 拼音码：收银台按首字母快速检索，长度与实体原注解保持一致
        e.Property(x => x.PinyinCode).HasMaxLength(20);
        // 条码唯一（过滤索引：仅非空条码参与唯一约束，允许多个无条码商品共存）
        e.HasIndex(x => x.Barcode).IsUnique().HasFilter("Barcode <> ''");
        e.Property(x => x.StockQuantity).HasPrecision(10, 3);
        e.Property(x => x.SafetyStock).HasPrecision(10, 3);
        e.Property(x => x.SalePrice).HasPrecision(10, 2);
        e.Property(x => x.CostPrice).HasPrecision(10, 2);
        e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
