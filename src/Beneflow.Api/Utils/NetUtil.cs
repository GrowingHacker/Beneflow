using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beneflow.Api.Utils;

/// <summary>局域网地址工具（QR 与 TLS 证书 SAN 需保持一致，统一从这里取）</summary>
public static class NetUtil
{
    /// <summary>取有默认网关的真实网卡 IPv4（排除回环/隧道/虚拟网卡），192 段优先</summary>
    public static List<IPAddress> LanIPv4s()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(n => new
            {
                HasGateway = n.GetIPProperties().GatewayAddresses.Any(),
                Addr = n.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address,
            })
            .Where(x => x.Addr != null)
            .OrderBy(x => x.HasGateway ? 0 : 1)          // 有网关在前（虚拟网卡一般无网关）
            .ThenBy(x => x.Addr!.GetAddressBytes()[0])   // 常见家庭网段：192 > 10 > 172
            .Select(x => x.Addr!)
            .Distinct()
            .ToList();
    }
}
