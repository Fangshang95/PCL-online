using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PCL.MCStudio;

/// <summary>
/// 联机服务器的证书信任：自签根 + 锁定（v50.12）。
///
/// 为什么不用公共 CA：服务器只有一个 IP、没有域名，公共 CA 签不了这种证书。
/// 所以这里不信系统信任库，只信下面这张根公钥——与更新清单"只信内置 ECDSA 公钥"
/// 是同一套信任模型（见 HotUpdateService.ManifestPublicKey）。根公钥是公开信息，
/// 进仓库不造成任何危害；**根私钥只存在于打包机，从不上网、不进仓库、不进发布包**。
///
/// 两道关卡，缺一不可：
///   1) 证书链必须止于上面这张根（别的根一律拒）；
///   2) 叶子证书的 SAN 必须包含我们正在连的那个 IP。
/// 第 2 条是第 1 条的兜底：万一根私钥泄露，攻击者签得出任意证书，但必须同时把
/// 客户端引到他的 IP 才能生效——远程攻击被降级成"需要改用户本地配置文件"。
/// </summary>
public static class ServerTrust
{
    /// <summary>内置根证书（DER 的 base64，492 字节，O=PClonline / CN=PClonline TLS Root CA）。
    /// 换根是硬变更：已分发的客户端认旧根，换掉就全部连不上，必须先发版再轮换。</summary>
    public const string RootCaDerBase64 =
        "MIIB6DCCAW+gAwIBAgIUUPwdeyiMkB3KzdK6NrfO1nmFT1EwCgYIKoZIzj0EAwMwNDESMBAGA1UECgwJ" +
        "UENsb25saW5lMR4wHAYDVQQDDBVQQ2xvbmxpbmUgVExTIFJvb3QgQ0EwHhcNMjYxMDEzMTIxNTEwWhcN" +
        "MzYxMDEwMTIyMDEwWjA0MRIwEAYDVQQKDAlQQ2xvbmxpbmUxHjAcBgNVBAMMFVBDbG9ubGluZSBUTFMg" +
        "Um9vdCBDQTB2MBAGByqGSM49AgEGBSuBBAAiA2IABKuA1kOKaWETO1be1V9oKH2kYuVzY5k9h0Ro5ZBQ" +
        "He9B5h79iBo2YSI2KkAtn6WmYOCeKS/YfOypY2/On+NJVIJTp1YGTlxPV4kp+sHsgqWXFeqa/YWVjS+G" +
        "bgYj6Pnl5qNCMEAwDwYDVR0TAQH/BAUwAwEB/zAOBgNVHQ8BAf8EBAMCAQYwHQYDVR0OBBYEFFhiUtBy" +
        "PLATYK2TWgtG/BMd/sp1MAoGCCqGSM49BAMDA2cAMGQCMBlfCaID2PXnw54jv3ZngzccPZR+3aNoLv8x" +
        "+UvNtct+5LRuBjeMJCX92VKSgZSTIQIwKidNJMgPn/5ZCmc81YKHUBGmRMOzKUFnWM8b3BPUKxn3Lrc4" +
        "BVFpmdIchZ7/eckp";

    /// <summary>根证书解析失败时整条 https 通路 fail-closed（返回 null → 一律拒），
    /// 明文配置照常工作，绝不能因为一张解析不动的证书把启动器带崩。</summary>
    private static readonly X509Certificate2? RootCa = Load(RootCaDerBase64);

    private static X509Certificate2? Load(string b64)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(b64));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 供 HttpClientHandler.ServerCertificateCustomValidationCallback 使用。
    /// root 参数默认是内置根；显式传入别的根只为验证工具能跑同一份判定代码，
    /// 业务调用一律不要传（传了就等于自己给自己发根）。
    /// </summary>
    public static bool Validate(string host, X509Certificate2? cert, SslPolicyErrors errors,
                                X509Certificate2? root = null)
    {
        root ??= RootCa;
        if (cert is null || root is null) return false;
        // 只把"根不在系统信任库里"这一项换掉，其余框架判定一律保留。
        // 整体放过 errors 等于没校验——中间人自签一张证书也能过。
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;
        if (!HostMatchesCertificate(host, cert)) return false;
        if (!ChainsToRoot(cert, root)) return false;
        return true;
    }

    /// <summary>证书链是否真的止于这张根。</summary>
    private static bool ChainsToRoot(X509Certificate2 cert, X509Certificate2 root)
    {
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.ExtraStore.Add(root);
        // 自签体系没有 CRL/OCSP 服务器可查；吊销要换根时走"发版换内置公钥"这条路
        policy.RevocationMode = X509RevocationMode.NoCheck;
        // 这张根不在系统信任库里，不放行"未知颁发机构"就永远建不起链。
        // 代价是"任何自签证书都能建链"，所以后面两步把它补回成真正的锁定：
        //   a) 链的最后一环必须与内置根同指纹；
        //   b) 除根上的 UntrustedRoot 外，任何环节有任何状态（过期、用途不符……）都拒。
        policy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        if (!chain.Build(cert)) return false;
        if (chain.ChainElements.Count == 0) return false;
        var last = chain.ChainElements[^1].Certificate;
        if (last.Thumbprint != root.Thumbprint) return false;
        for (var i = 0; i < chain.ChainElements.Count; i++)
        {
            var isRoot = i == chain.ChainElements.Count - 1;
            foreach (var s in chain.ChainElements[i].ChainElementStatus)
                if (!Allowed(s.Status, isRoot)) return false;
        }
        foreach (var s in chain.ChainStatus)
            if (!Allowed(s.Status, true)) return false;
        return true;
    }

    private static bool Allowed(X509ChainStatusFlags status, bool onRoot)
        => status == X509ChainStatusFlags.NoError
           || (onRoot && status == X509ChainStatusFlags.UntrustedRoot);

    /// <summary>
    /// 只认 SAN 里的 iPAddress / dNSName，**绝不退回去比 CN**：CN 是给人看的，
    /// 不是身份凭据（RFC 6125 也把 SAN 定为唯一权威来源）。SAN 里没有对应条目就拒。
    /// </summary>
    public static bool HostMatchesCertificate(string host, X509Certificate2 cert)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var dns = new List<string>();
        var ips = new List<IPAddress>();
        foreach (var ext in cert.Extensions)
            if (ext.Oid?.Value == OidSubjectAltName)
                ParseGeneralNames(ext.RawData, dns, ips);

        // IPv6 在 URL 里带方括号，进证书比对前要摘掉
        var bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (IPAddress.TryParse(bare, out var want))
        {
            foreach (var ip in ips)
                if (ip.Equals(want)) return true;
            return false;
        }
        foreach (var n in dns)
            if (string.Equals(n, bare, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private const string OidSubjectAltName = "2.5.29.17";
    private const byte TagDnsName = 0x82;
    private const byte TagIpAddress = 0x87;

    /// <summary>手解 GeneralNames 的 DER。.NET 没有公开的 SAN 强类型访问器，
    /// 而这一条判定不能交给"看起来对"的字符串匹配，所以直接读标签长度值。</summary>
    private static void ParseGeneralNames(byte[] der, List<string> dns, List<IPAddress> ips)
    {
        var p = 0;
        if (der.Length < 2 || der[p++] != 0x30) return;     // SEQUENCE
        var total = ReadLength(der, ref p);
        if (total < 0) return;
        var end = Math.Min(der.Length, p + total);
        while (p + 1 < end)
        {
            var tag = der[p++];
            var len = ReadLength(der, ref p);
            if (len < 0 || p + len > end) return;            // 结构不完整：当作没有
            switch (tag)
            {
                case TagDnsName:
                    dns.Add(Encoding.ASCII.GetString(der, p, len));
                    break;
                case TagIpAddress:
                    if (len is 4 or 16)                      // v4 / v6 字节长度
                        ips.Add(new IPAddress(der[p..(p + len)]));
                    break;
            }
            p += len;
        }
    }

    /// <summary>DER 长度字段：短形式或长形式（0x81/0x82…）。不定长（0x80）在证书里非法。</summary>
    private static int ReadLength(byte[] der, ref int p)
    {
        if (p >= der.Length) return -1;
        var b = der[p++];
        if (b < 0x80) return b;
        if (b == 0x80) return -1;
        var n = b & 0x7F;
        if (n is 0 or > 4 || p + n > der.Length) return -1;
        var v = 0;
        for (var k = 0; k < n; k++) v = (v << 8) | der[p++];
        return v < 0 ? -1 : v;
    }
}
