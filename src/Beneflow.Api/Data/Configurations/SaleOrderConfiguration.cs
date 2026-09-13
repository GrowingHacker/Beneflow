using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>销售单主表配置。</summary>
public class SaleOrderConfiguration : IEntityTypeConfiguration<SaleOrder>
{
    public void Configure(EntityTypeBuilder<SaleOrder> e)
    {
        e.Property(x => x.OrderNo).HasMaxLength(20);
        e.HasIndex(x => x.OrderNo).IsUnique();
        // 列表/导出/报表/仪表盘按 CreatedAt 日期范围过滤，单号生成按当日计数
        e.HasIndex(x => x.CreatedAt);
        e.Property(x => x.TotalAmount).HasPrecision(12, 2);
        e.Property(x => x.DiscountAmount).HasPrecision(12, 2);
        e.Property(x => x.PayAmount).HasPrecision(12, 2);
        e.Property(x => x.CashAmount).HasPrecision(12, 2);
        e.Property(x => x.ChangeAmount).HasPrecision(12, 2);
        e.Property(x => x.PayMethod).HasMaxLength(20);
        e.Property(x => x.WechatId).HasMaxLength(50);
    }
}
