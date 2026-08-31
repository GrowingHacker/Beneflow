using System.Collections.Concurrent;
using Beneflow.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>
/// 账号状态缓存：认证通过后的每个请求据此拦截「已禁用 / 已删除」账号的旧 token，
/// 解决 JWT 有效期内禁用不踢人的问题。缓存 30 秒；启停、删除用户时主动失效，立即生效。
/// </summary>
public class AccountStatusCache
{
    private sealed record Entry(bool Active, DateTime At);
    private readonly ConcurrentDictionary<int, Entry> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>用户当前是否处于「未删除且启用」状态（带 30s 缓存）</summary>
    public async Task<bool> IsActiveAsync(AppDbContext db, int userId)
    {
        if (userId <= 0) return false;
        if (_cache.TryGetValue(userId, out var e) && DateTime.Now - e.At < Ttl)
            return e.Active;

        var active = await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == userId && !u.IsDeleted && u.Status);
        _cache[userId] = new Entry(active, DateTime.Now);
        return active;
    }

    /// <summary>状态发生变更（启停 / 删除）后调用，使下一次请求立即重新查库</summary>
    public void Invalidate(int userId) => _cache.TryRemove(userId, out _);
}
