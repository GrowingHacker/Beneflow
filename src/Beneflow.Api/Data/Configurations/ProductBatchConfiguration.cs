using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>商品批次表配置（有效期管理；进货按批次入库）。</summary>
public class ProductBatchConfiguration : IEntityTypeConfiguration<ProductBatch>
{
    public void Configure(EntityTypeBuilder<ProductBatch> e)
    {
        e.Property(x => x.BatchNo).HasMaxLength(30);
        e.Property(x => x.Quantity).HasPrecision(10, 3);
        // 临期/过期统计与临期列表按 ExpireDate 范围查询、排序
        e.HasIndex(x => x.ExpireDate);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
