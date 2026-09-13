using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>盘点单主表配置。</summary>
public class StockCheckConfiguration : IEntityTypeConfiguration<StockCheck>
{
    public void Configure(EntityTypeBuilder<StockCheck> e)
    {
        e.Property(x => x.OrderNo).HasMaxLength(20);
        e.HasIndex(x => x.OrderNo).IsUnique();
        e.Property(x => x.Range).HasMaxLength(50);
        e.Property(x => x.ProfitQty).HasPrecision(10, 3);
        e.Property(x => x.LossQty).HasPrecision(10, 3);
    }
}
