using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beneflow.Api.Data.Configurations;

/// <summary>操作日志表配置。</summary>
public class OperationLogConfiguration : IEntityTypeConfiguration<OperationLog>
{
    public void Configure(EntityTypeBuilder<OperationLog> e)
    {
        // 日志查询按 CreatedAt 日期范围过滤 + 分页，表只增不删
        e.HasIndex(x => x.CreatedAt);
    }
}
