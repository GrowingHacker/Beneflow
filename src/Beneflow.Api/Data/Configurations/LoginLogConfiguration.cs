using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>
/// 登录日志表配置。
/// 该表目前完全依赖 EF 约定（主键 Id、UserName 长度、CreatedAt 类型），无需额外配置。
/// 保留此文件是为了让「实体 ↔ 配置」一一对应：新增配置时有明确的落点，
/// 也便于对照发现漏配的实体。
/// </summary>
public class LoginLogConfiguration : IEntityTypeConfiguration<LoginLog>
{
    public void Configure(EntityTypeBuilder<LoginLog> e)
    {
        // 故意留空：仅使用约定映射。
        // 若日后按 CreatedAt 范围查询登录日志，可在此处补 e.HasIndex(x => x.CreatedAt)。
    }
}
