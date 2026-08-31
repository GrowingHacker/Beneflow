using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Beneflow.Api.Services;

/// <summary>
/// 本地离线 TLS：
/// 1. 首次运行生成自签根 CA（缓存于 %LocalAppData%\Beneflow\tls）
/// 2. 按当前局域网 IP 动态签发含 SAN（localhost + 各 IP）的服务器证书
/// 3. 手机安装根 CA 证书后，https 访问即受信，从而获得摄像头等安全上下文能力
/// </summary>
public class TlsCertService : ITlsCertService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beneflow", "tls");
    private const string PfxPass = "Beneflow-local";
    private const string CaFile = "beneflow-root-ca.pfx";

    public X509Certificate2? CaCert { get; private set; }
    public X509Certificate2? ServerCert { get; private set; }
    public bool Ready => ServerCert != null;

    /// <summary>初始化 CA 与服务器证书；任一异常都只降级为纯 HTTP</summary>
    public void Setup(IReadOnlyCollection<IPAddress> lanIps, string hostName)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            CaCert = LoadOrCreateCa();
            ServerCert = LoadOrCreateLeaf(lanIps, hostName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "TLS 证书初始化失败，本次仅提供 HTTP；目录 {Dir}", Dir);
            CaCert = null;
            ServerCert = null;
        }
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        var path = Path.Combine(Dir, CaFile);
        if (File.Exists(path))
        {
            var existing = new X509Certificate2(path, PfxPass);
            if (DateTime.UtcNow < existing.NotAfter.ToUniversalTime()) return existing;
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=Beneflow Local Root CA,O=Beneflow,C=CN",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        // 生成本地根证书，有效期 15 年
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(15));
        var ca = new X509Certificate2(self.Export(X509ContentType.Pfx, PfxPass), PfxPass,
            X509KeyStorageFlags.Exportable);
        File.WriteAllBytes(path, ca.Export(X509ContentType.Pfx, PfxPass));
        return ca;
    }

    private X509Certificate2 LoadOrCreateLeaf(IReadOnlyCollection<IPAddress> lanIps, string hostName)
    {
        if (CaCert == null) throw new InvalidOperationException("CA 未就绪");

        // 叶子证书按「IP 集合指纹」缓存：网卡变化则重新签发
        var ips = lanIps.OrderBy(a => a.ToString()).ToList();
        var finger = string.Join(",", ips.Select(a => a.ToString()));
        var path = Path.Combine(Dir, $"server-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(finger)))[..16]}.pfx");

        if (File.Exists(path))
        {
            var old = new X509Certificate2(path, PfxPass);
            // 仍在有效期且 SAN 覆盖全部当前 IP 则复用
            if (DateTime.UtcNow < old.NotAfter.ToUniversalTime() && Covers(old, ips, hostName))
                return old;
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Beneflow-Server,O=Beneflow,C=CN", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(hostName);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in ips) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true)); // serverAuth

        var now = DateTimeOffset.UtcNow.AddDays(-1);
        // 注意：Create(issuerCert,...) 返回值不含主题私钥，必须显式关联后再导出
        using var pubOnly = req.Create(CaCert, now, now.AddDays(730),
            RandomNumberGenerator.GetBytes(8));
        using var withKey = pubOnly.CopyWithPrivateKey(rsa);

        var leaf = new X509Certificate2(withKey.Export(X509ContentType.Pfx, PfxPass), PfxPass,
            X509KeyStorageFlags.Exportable);
        File.WriteAllBytes(path, leaf.Export(X509ContentType.Pfx, PfxPass));

        // 清理过期的旧叶子缓存
        foreach (var f in Directory.GetFiles(Dir, "server-*.pfx"))
            if (!string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch { }

        return leaf;
    }

    private static bool Covers(X509Certificate2 cert, List<IPAddress> ips, string hostName)
    {
        var sanExt = cert.Extensions.OfType<X509Extension>()
            .FirstOrDefault(e => e.Oid?.Value == "2.5.29.17"); // subjectAltName
        if (sanExt == null) return false;

        var raw = new System.Security.Cryptography.AsnEncodedData(sanExt.Oid!, sanExt.RawData);
        var text = raw.Format(false);
        return ips.All(ip => text.Contains(ip.ToString(), StringComparison.OrdinalIgnoreCase)) &&
               text.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
