using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>用户表配置。</summary>
public class UserInfoConfiguration : IEntityTypeConfiguration<UserInfo>
{
    public void Configure(EntityTypeBuilder<UserInfo> e)
    {
        e.ToTable("UserInfo");
        e.Property(x => x.Username).HasMaxLength(20);
        e.HasIndex(x => x.Username).IsUnique();
        e.Property(x => x.Phone).HasMaxLength(20);
        e.Property(x => x.Salt).HasMaxLength(64);
        e.Property(x => x.PasswordHash).HasMaxLength(256);
    }
}
