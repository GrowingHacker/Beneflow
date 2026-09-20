using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>销售单明细表配置（成本价为销售时快照，用于毛利核算）。</summary>
public class SaleOrderDetailConfiguration : IEntityTypeConfiguration<SaleOrderDetail>
{
    public void Configure(EntityTypeBuilder<SaleOrderDetail> e)
    {
        // 条码快照：原先长度只由实体上的 [StringLength(20)] 提供，
        // 迁到 Fluent 后必须显式声明，否则列会退化为 nvarchar(max) 并产生非预期迁移。
        e.Property(x => x.Barcode).HasMaxLength(20);
        e.Property(x => x.Quantity).HasPrecision(10, 3);
        e.Property(x => x.UnitPrice).HasPrecision(10, 2);
        e.Property(x => x.OriginalPrice).HasPrecision(10, 2);
        e.Property(x => x.CostPrice).HasPrecision(10, 2);
        e.Property(x => x.SubTotal).HasPrecision(12, 2);
        e.Property(x => x.ReturnedQuantity).HasPrecision(10, 3).HasDefaultValueSql("(0)");
    }
}
