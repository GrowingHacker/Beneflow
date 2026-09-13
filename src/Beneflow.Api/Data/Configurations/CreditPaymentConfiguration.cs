using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>赊账还款记录表配置。</summary>
public class CreditPaymentConfiguration : IEntityTypeConfiguration<CreditPayment>
{
    public void Configure(EntityTypeBuilder<CreditPayment> e)
    {
        e.Property(x => x.PayAmount).HasPrecision(12, 2);
        e.Property(x => x.PayMethod).HasMaxLength(20);
    }
}
