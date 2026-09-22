using Beneflow.Api.Models;

namespace Beneflow.Api.Services;

/// <summary>操作日志：查询 + 各业务服务统一写入入口</summary>
public interface ILogService
{
    Task<PagedResult<OperationLogItemDto>> QueryAsync(string? keyword, DateTime? from, DateTime? to, int page, int pageSize);

    /// <summary>追加一条操作日志（仅加入上下文，随调用方业务一起 SaveChanges）</summary>
    Task WriteAsync(string module, string action, string? target);
}
