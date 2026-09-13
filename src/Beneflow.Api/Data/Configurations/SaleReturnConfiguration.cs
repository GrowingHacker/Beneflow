using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>销售退货单主表配置（顾客退款，入库回补）。</summary>
public class SaleReturnConfiguration : IEntityTypeConfiguration<SaleReturn>
{
    public void Configure(EntityTypeBuilder<SaleReturn> e)
    {
        e.Property(x => x.OrderNo).HasMaxLength(20);
        e.HasIndex(x => x.OrderNo).IsUnique();
        e.Property(x => x.RefundAmount).HasPrecision(12, 2);
    }
}
