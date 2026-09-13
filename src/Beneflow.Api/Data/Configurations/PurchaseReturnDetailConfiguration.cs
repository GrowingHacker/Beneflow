using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>采购退货单明细表配置。</summary>
public class PurchaseReturnDetailConfiguration : IEntityTypeConfiguration<PurchaseReturnDetail>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnDetail> e)
    {
        // 作废守卫按 ReturnId 关联子查询（ProductId 复合覆盖退货商品判定）
        e.HasIndex(x => new { x.ReturnId, x.ProductId });
        e.Property(x => x.Qty).HasPrecision(10, 3);
        e.Property(x => x.CostPrice).HasPrecision(10, 2);
        e.Property(x => x.SubTotal).HasPrecision(12, 2);
    }
}
