using System.Security.Cryptography;

namespace PClonlineSign;

/// <summary>
/// PClonline 更新签名工具（内部使用，不随发行包分发）。
///
/// 用法：
///   keygen &lt;私钥输出文件&gt; &lt;公钥输出文件&gt;   生成 ECDSA P-256 密钥对（私钥 PKCS#8 PEM，公钥 SPKI base64）
///   sign   &lt;私钥文件&gt; &lt;待签文件&gt; &lt;签名输出文件&gt;
///   verify &lt;公钥文件&gt; &lt;原文件&gt; &lt;签名文件&gt;
///
/// 签名格式：ECDSA P-256 + SHA-256，签名字节为 IEEE P1363（r||s，64 字节），与
/// .NET 端 ECDsa.SignData / VerifyData 的默认格式一致，客户端可直接验签。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法：keygen <私钥> <公钥> | sign <私钥> <文件> <签名> | verify <公钥> <文件> <签名>");
            return 1;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "keygen":
                    return Keygen(args[1], args[2]);
                case "sign":
                    return Sign(args[1], args[2], args[3]);
                case "verify":
                    return Verify(args[1], args[2], args[3]);
                default:
                    Console.Error.WriteLine("未知命令：" + args[0]);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败：" + ex.Message);
            return 2;
        }
    }

    private static int Keygen(string privatePath, string publicPath)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, ecdsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicPath, Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        Console.WriteLine("已生成密钥对：" + privatePath + " / " + publicPath);
        return 0;
    }

    private static int Sign(string privatePath, string filePath, string sigPath)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(StripPem(File.ReadAllText(privatePath))), out _);
        var sig = ecdsa.SignData(File.ReadAllBytes(filePath), HashAlgorithmName.SHA256);
        File.WriteAllText(sigPath, Convert.ToBase64String(sig));
        Console.WriteLine("已签名：" + sigPath);
        return 0;
    }

    private static int Verify(string publicPath, string filePath, string sigPath)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(File.ReadAllText(publicPath).Trim()), out _);
        var ok = ecdsa.VerifyData(File.ReadAllBytes(filePath),
            Convert.FromBase64String(File.ReadAllText(sigPath).Trim()), HashAlgorithmName.SHA256);
        Console.WriteLine(ok ? "验签通过" : "验签失败");
        return ok ? 0 : 3;
    }

    /// <summary>去掉 PEM 头尾与换行，得到纯 base64。</summary>
    private static string StripPem(string pem)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in pem.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("-----", StringComparison.Ordinal)) continue;
            sb.Append(t);
        }
        return sb.ToString();
    }
}
