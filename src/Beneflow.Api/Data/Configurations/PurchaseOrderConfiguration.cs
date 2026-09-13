using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>进货单主表配置。</summary>
public class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> e)
    {
        e.Property(x => x.OrderNo).HasMaxLength(20);
        e.HasIndex(x => x.OrderNo).IsUnique();
        // 列表/导出/报表按 CreatedAt 日期范围过滤，单号生成按当日计数
        e.HasIndex(x => x.CreatedAt);
        e.Property(x => x.TotalQty).HasPrecision(10, 3);
        e.Property(x => x.TotalAmount).HasPrecision(12, 2);
    }
}
