using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>菜单/权限表配置。</summary>
public class MenuConfiguration : IEntityTypeConfiguration<Menu>
{
    public void Configure(EntityTypeBuilder<Menu> e)
    {
        e.Property(x => x.Name).HasMaxLength(50);
        e.Property(x => x.PermCode).HasMaxLength(50);
        e.Property(x => x.Type).HasMaxLength(10);
        e.HasIndex(x => x.PermCode);
    }
}
