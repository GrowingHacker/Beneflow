using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>库存流水表配置（所有库存变动的唯一凭据）。</summary>
public class StockLogConfiguration : IEntityTypeConfiguration<StockLog>
{
    public void Configure(EntityTypeBuilder<StockLog> e)
    {
        e.Property(x => x.ChangeType).HasMaxLength(20);
        e.Property(x => x.ChangeQty).HasPrecision(10, 3);
        e.Property(x => x.BeforeQty).HasPrecision(10, 3);
        e.Property(x => x.AfterQty).HasPrecision(10, 3);
        e.Property(x => x.RefNo).HasMaxLength(30);
        e.HasIndex(x => new { x.ProductId, x.CreatedAt });
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
