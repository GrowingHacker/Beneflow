using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Beneflow.Api.Services;

/// <summary>本地离线 TLS 证书服务：自签根 CA + 按局域网 IP 动态签发服务器证书</summary>
public interface ITlsCertService
{
    X509Certificate2? CaCert { get; }
    X509Certificate2? ServerCert { get; }
    bool Ready { get; }

    /// <summary>初始化 CA 与服务器证书；任一异常都只降级为纯 HTTP</summary>
    void Setup(IReadOnlyCollection<IPAddress> lanIps, string hostName);
}
