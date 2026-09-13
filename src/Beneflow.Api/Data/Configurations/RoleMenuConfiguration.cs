using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>角色-菜单权限关联表配置。</summary>
public class RoleMenuConfiguration : IEntityTypeConfiguration<RoleMenu>
{
    public void Configure(EntityTypeBuilder<RoleMenu> e)
    {
        e.HasKey(x => new { x.RoleId, x.MenuId });
        e.HasOne(x => x.Menu).WithMany().HasForeignKey(x => x.MenuId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
