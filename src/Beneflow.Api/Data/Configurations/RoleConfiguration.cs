using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>角色表配置。</summary>
public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> e)
    {
        e.Property(x => x.Name).HasMaxLength(50);
        e.Property(x => x.Code).HasMaxLength(30);
        e.HasIndex(x => x.Code).IsUnique();
    }
}
