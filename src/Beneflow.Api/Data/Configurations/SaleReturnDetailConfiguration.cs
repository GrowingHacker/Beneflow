using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>销售退货单明细表配置。</summary>
public class SaleReturnDetailConfiguration : IEntityTypeConfiguration<SaleReturnDetail>
{
    public void Configure(EntityTypeBuilder<SaleReturnDetail> e)
    {
        e.Property(x => x.Qty).HasPrecision(10, 3);
        e.Property(x => x.UnitPrice).HasPrecision(10, 2);
        e.Property(x => x.SubTotal).HasPrecision(12, 2);
    }
}
