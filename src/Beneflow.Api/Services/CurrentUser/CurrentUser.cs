using System.Net;
using System.Security.Claims;

namespace Beneflow.Api.Services;

/// <summary>当前登录用户上下文（从 JWT Claims 解析）</summary>
public interface ICurrentUser
{
    int Id { get; }
    string Username { get; }
    /// <summary>客户端 IP（无上下文时为 null）</summary>
    string? ClientIp { get; }
}

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public int Id
    {
        get
        {
            var v = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(v, out var id) ? id : 0;
        }
    }

    public string Username => Principal?.FindFirstValue(ClaimTypes.Name) ?? "";

    public string? ClientIp
    {
        get
        {
            var ctx = _accessor.HttpContext;
            if (ctx == null) return null;

            // 1) 优先从标准代理头取（反向代理 / IIS / Nginx 转发场景）
            var xff = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(xff))
            {
                var first = xff.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).FirstOrDefault();
                if (first != null && TryNormalizeIp(first, out var norm)) return norm;
            }
            var xri = ctx.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(xri) && TryNormalizeIp(xri.Trim(), out var norm2))
                return norm2;

            // 2) 兜底：直连场景下的 RemoteIpAddress
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip == null) return null;
            return NormalizeIpAddress(ip);
        }
    }

    /// <summary>把字符串形式的 IP（代理头里取出来的）归一化为 IPv4 文本；IPv6 回环和映射地址都会转成点分十进制</summary>
    private static bool TryNormalizeIp(string s, out string result)
    {
        if (IPAddress.TryParse(s, out var addr))
        {
            result = NormalizeIpAddress(addr);
            return true;
        }
        result = "";
        return false;
    }

    /// <summary>把 System.Net.IPAddress 归一化为友好的 IPv4 文本表示</summary>
    private static string NormalizeIpAddress(IPAddress ip)
    {
        // 纯 IPv6 回环 ::1 → 等价 127.0.0.1
        if (IPAddress.IsLoopback(ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return "127.0.0.1";
        // IPv4-mapped IPv6 (::ffff:x.x.x.x) → 点分十进制
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }
}
