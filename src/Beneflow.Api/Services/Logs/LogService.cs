using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>操作日志（不可删除，仅查看）</summary>
public class LogService : ILogService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    public LogService(AppDbContext db, ICurrentUser me) { _db = db; _me = me; }

    public async Task<PagedResult<OperationLogItemDto>> QueryAsync(string? keyword, DateTime? from, DateTime? to, int page, int pageSize)
    {
        var q = _db.OperationLogs.AsNoTracking()
            .Where(l => string.IsNullOrEmpty(keyword)
                        || l.UserName.Contains(keyword!) || l.Module.Contains(keyword!)
                        || l.Action.Contains(keyword!) || (l.Target != null && l.Target.Contains(keyword!)));
        if (from.HasValue) q = q.Where(l => l.CreatedAt >= from.Value.Date);
        if (to.HasValue) q = q.Where(l => l.CreatedAt < to.Value.Date.AddDays(1));

        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.OrderByDescending(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var list = rows.Select(l => new OperationLogItemDto
        {
            Id = l.Id, UserName = l.UserName, Module = l.Module, Action = l.Action,
            Target = l.Target, Ip = l.IpAddress, CreatedAt = l.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        }).ToList();
        return new PagedResult<OperationLogItemDto> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    public Task WriteAsync(string module, string action, string? target)
    {
        _db.OperationLogs.Add(new OperationLog
        {
            UserId = _me.Id, UserName = _me.Username, IpAddress = _me.ClientIp,
            Module = module, Action = action, Target = target,
        });
        return Task.CompletedTask;
    }
}
