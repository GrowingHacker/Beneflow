using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>用户-角色关联表配置。</summary>
public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> e)
    {
        e.HasKey(x => new { x.UserId, x.RoleId });
        e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
