using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>盘点单明细表配置。</summary>
public class StockCheckDetailConfiguration : IEntityTypeConfiguration<StockCheckDetail>
{
    public void Configure(EntityTypeBuilder<StockCheckDetail> e)
    {
        // 无 Check 导航属性，EF 不会自动建外键索引；盘点确认按 CheckId 查明细
        e.HasIndex(x => x.CheckId);
        e.Property(x => x.BookQty).HasPrecision(10, 3);
        e.Property(x => x.ActualQty).HasPrecision(10, 3);
        e.Property(x => x.DiffQty).HasPrecision(10, 3);
    }
}
