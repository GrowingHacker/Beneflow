using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>赊账记录表配置（以顾客微信号作为欠款标识）。</summary>
public class CreditSaleConfiguration : IEntityTypeConfiguration<CreditSale>
{
    public void Configure(EntityTypeBuilder<CreditSale> e)
    {
        // 赊账列表/导出/欠款报表按 CreatedAt 日期范围过滤
        e.HasIndex(x => x.CreatedAt);
        // 微信号与手机号：原先长度只由实体注解提供，迁到 Fluent 后显式保留
        e.Property(x => x.WechatId).HasMaxLength(50);
        e.Property(x => x.Phone).HasMaxLength(20);
        e.Property(x => x.CreditAmount).HasPrecision(12, 2);
        e.Property(x => x.PaidAmount).HasPrecision(12, 2);
        e.Property(x => x.RemainingAmount).HasPrecision(12, 2);
    }
}
