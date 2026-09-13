using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>采购退货单主表配置（退给供应商，出库）。</summary>
public class PurchaseReturnConfiguration : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> e)
    {
        e.Property(x => x.OrderNo).HasMaxLength(20);
        e.HasIndex(x => x.OrderNo).IsUnique();
        e.Property(x => x.RefundAmount).HasPrecision(12, 2);
    }
}
