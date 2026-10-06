using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Ae.Dns.Client;
using Ae.Dns.Protocol;
using Ae.Dns.Protocol.Enums;
using Ae.Dns.Protocol.Records;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;

namespace PCL.Core.IO.Net.Dns;

public class DnsQuery : IDisposable
{
    private const string ModuleName = "DoH query";
    public static DnsQuery Instance { get; } = new();

    private readonly DnsCachingClient _resolver;
    private readonly HttpClient[] _httpClients;

    private DnsQuery()
    {
        var proxyHandler = new HttpClientHandler()
        {
            Proxy = HttpProxyManager.Instance
        };
        // 使用Ae.Dns创建DoH客户端，支持多个DoH服务器
        _httpClients =
        [
            new HttpClient(proxyHandler)
            {
                BaseAddress = new Uri("https://doh.pub/")
            },
            new HttpClient(proxyHandler)
            {
                BaseAddress = new Uri("https://doh.pysio.online/")
            },
            new HttpClient(proxyHandler)
            {
                BaseAddress = new Uri("https://cloudflare-dns.com/")
            }
        ];
        _resolver = new DnsCachingClient(
            new WeightedDnsRacerClient(2, _httpClients.Select(static x => new DnsHttpClient(x)).ToArray<IDnsClient>()),
            new MemoryCache("DoH Query Cache"));
    }

    public async Task<DnsMessage?> QueryAsync(string host, DnsQueryType qType, CancellationToken cts = default)
    {
        try
        {
            return await _resolver.Query(DnsQueryFactory.CreateQuery(host, qType), cts);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ModuleName, $"Failed to resolve DNS for {host}: {ex.Message}, use system default DNS");
        }

        return null;
    }

    public async Task<IPAddress[]?> QueryForIpAsync(string host, CancellationToken cts = default)
    {
        var queryResponse = await Task.WhenAll(
            [
                QueryAsync(host, DnsQueryType.A, cts),
                QueryAsync(host, DnsQueryType.AAAA, cts)
            ]
        );

        var answers = queryResponse.Where(static x => x is not null)
            .SelectMany(static x => x!.Answers)
            .Where(static x => x.Type is DnsQueryType.A or DnsQueryType.AAAA)
            .Select(static x => x.Resource as DnsIpAddressResource)
            .Where(static x => x is not null)
            .Select(static x => x!.IPAddress)
            .ToArray();

        // DoH 拿到响应但**没有任何 A/AAAA 记录**时，同样要回退系统 DNS。
        // 原实现在这里直接返回空数组，于是 HostConnectionHandler 会抛
        // "DNS resolution failed for {host}" 且不重试——对 BMCLAPI 这类
        // 会 302 到 open-bmcl-api.lunar.cc 等第三方域名的镜像站点，
        // 表现为下载源无故超时、反复换源重下。
        if (answers.Length == 0)
        {
            LogWrapper.Warn(ModuleName,
                $"DoH returned no A/AAAA answer for {host}, use system default DNS");
            try
            {
                return await System.Net.Dns.GetHostAddressesAsync(host, cts);
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ModuleName,
                    $"System DNS also failed for {host}: {ex.Message}");
                return answers;
            }
        }

        return answers;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var client in _httpClients)
        {
            client.Dispose();
        }
        _resolver.Dispose(); // 好像这个包的 Dispose 并没有做什么 lol
    }
}