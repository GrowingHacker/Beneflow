using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>系统参数表配置（分组键-JSON 值存储）。</summary>
public class SystemConfigConfiguration : IEntityTypeConfiguration<SystemConfig>
{
    public void Configure(EntityTypeBuilder<SystemConfig> e)
    {
        // ConfigKey 原先的长度只由实体上的 [MaxLength(50)] 提供，
        // 迁到 Fluent 后必须显式声明，否则列会退化为 nvarchar(max) 并产生非预期迁移。
        e.Property(x => x.ConfigKey).HasMaxLength(50);
    }
}
