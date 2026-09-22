using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>销售单支付明细表配置（混合支付：一笔单多种收款方式，一行一个方式）。</summary>
public class SaleOrderPaymentConfiguration : IEntityTypeConfiguration<SaleOrderPayment>
{
    public void Configure(EntityTypeBuilder<SaleOrderPayment> e)
    {
        e.Property(x => x.PayMethod).HasMaxLength(20);
        e.Property(x => x.Amount).HasPrecision(12, 2);
        // 列表/详情都按订单取整批支付行，走这个索引；订单不物理删除，故无需级联顾虑
        e.HasIndex(x => x.OrderId);
    }
}
