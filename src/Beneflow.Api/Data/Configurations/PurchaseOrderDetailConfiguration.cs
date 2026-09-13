using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>进货单明细表配置。</summary>
public class PurchaseOrderDetailConfiguration : IEntityTypeConfiguration<PurchaseOrderDetail>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderDetail> e)
    {
        // 无 Order 导航属性，EF 不会自动建外键索引；列表聚合/明细/作废/编辑均按 OrderId 查询
        e.HasIndex(x => x.OrderId);
        e.Property(x => x.Qty).HasPrecision(10, 3);
        e.Property(x => x.CostPrice).HasPrecision(10, 2);
        e.Property(x => x.SubTotal).HasPrecision(12, 2);
    }
}
