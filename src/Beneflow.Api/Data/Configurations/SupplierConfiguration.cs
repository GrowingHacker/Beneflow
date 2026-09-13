using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>供应商表配置。</summary>
public class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> e)
    {
        e.Property(x => x.Name).HasMaxLength(100);
        e.HasIndex(x => x.Name).IsUnique();
        e.Property(x => x.Contact).HasMaxLength(50);
        e.Property(x => x.Phone).HasMaxLength(20);
    }
}
