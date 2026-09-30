using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Buffers.Binary;
using Open.Nat;

namespace PCL.MCStudio;

/// <summary>
/// P1-D 主动端口映射：UPnP / NAT-PMP（Open.NAT 库）+ PCP（RFC 6887，手写客户端，
/// Open.NAT 2.1 并不支持 PCP）。两路并行竞速，任一先成功即返回外部端口。
/// 路由器支持时为打洞 socket 开一条「外部端口 → 本地端口」的 UDP 转发，
/// 对端直连该映射即可入站，完全绕过打洞。设备发现进程级只做一次（≤4s），
/// 不支持/被禁用时静默返回 0，调用方退回普通打洞流程，零副作用。
/// </summary>
public static class NatPortMapper
{
    private static NatDevice? _device;
    private static bool _probed;                       // 失败也标记，避免每次连接重复 4s 发现
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// 为本地 UDP socket 创建外部端口映射，成功返回外部端口（0=不可用）。
    /// 优先请求与本地相同的外部端口，被占用/拒绝时由路由器自行分配（读回实际值）。
    /// 映射带 7200s 生命周期自动过期，不做后台续约（打洞会话远短于此）。
    /// </summary>
    public static async Task<int> MapAsync(Socket udp, CancellationToken ct)
    {
        var localPort = ((IPEndPoint)udp.LocalEndPoint!).Port;
        // Open.NAT（UPnP→NAT-PMP）与 PCP 并行竞速：总墙钟不超过较慢一路（≤5s 预算内）
        var natTask = MapOpenNatAsync(localPort, ct);
        var pcpTask = MapPcpAsync(localPort, ct);
        var winner = await Task.WhenAny(natTask, pcpTask).ConfigureAwait(false);
        var port = await winner.ConfigureAwait(false);
        if (port > 0) return port;
        return await (ReferenceEquals(winner, natTask) ? pcpTask : natTask).ConfigureAwait(false);
    }

    private static async Task<int> MapOpenNatAsync(int localPort, CancellationToken ct)
    {
        try
        {
            var device = await GetDeviceAsync(ct).ConfigureAwait(false);
            if (device is null) return 0;
            var mapping = new Mapping(Protocol.Udp, localPort, localPort, 7200, "MCStudio P2P");
            await device.CreatePortMapAsync(mapping).ConfigureAwait(false);
            return mapping.PublicPort > 0 ? mapping.PublicPort : localPort;
        }
        catch
        {
            return 0;                                  // 无路由器权限/不支持/超时——静默降级
        }
    }

    /// <summary>设备发现（一次）：先试 UPnP(SSDP)，未中再试 NAT-PMP，任一命中即缓存。</summary>
    private static async Task<NatDevice?> GetDeviceAsync(CancellationToken ct)
    {
        if (_probed) return _device;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_probed) return _device;
            var discoverer = new NatDiscoverer();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(4000);
            try
            {
                _device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts).ConfigureAwait(false);
            }
            catch
            {
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(4000);
                _device = await discoverer.DiscoverDeviceAsync(PortMapper.Pmp, cts2).ConfigureAwait(false);
            }
            return _device;
        }
        catch
        {
            _device = null;
            return _device;
        }
        finally
        {
            _probed = true;
            _lock.Release();
        }
    }

    // ---------------- PCP（RFC 6887 MAP）手写客户端 ----------------

    private const int PcpPort = 5351;
    private const int PcpLifetime = 7200;

    /// <summary>对默认网关发 PCP MAP 请求（UDP 5351）。1.5s 超时重试 1 次。</summary>
    private static async Task<int> MapPcpAsync(int localPort, CancellationToken ct)
    {
        var gateway = GetDefaultGateway();
        if (gateway is null) return 0;
        for (var attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
        {
            var port = await PcpMapOnceAsync(gateway, localPort, ct).ConfigureAwait(false);
            if (port > 0) return port;
            try { await Task.Delay(300, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return 0;
    }

    private static async Task<int> PcpMapOnceAsync(IPAddress gateway, int localPort, CancellationToken ct)
    {
        // nonce 用于匹配请求与响应（防串包）；GUID 字节做熵源
        var nonce = Guid.NewGuid().ToByteArray();
        var req = new byte[60];
        req[0] = 2;                                    // PCP version 2（v1 已废除）
        req[1] = 1;                                    // MAP opcode
        BinaryPrimitives.WriteInt32BigEndian(req.AsSpan(4), PcpLifetime);
        // req[8..23] client IP = 0（表示发送方自身）
        nonce.AsSpan(0, 12).CopyTo(req.AsSpan(24, 12));
        req[36] = 17;                                  // UDP
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(40), (ushort)localPort);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(42), (ushort)localPort); // 期望同号外部端口
        // req[44..59] suggested external IP = 0（路由器自选）

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            await udp.SendToAsync(req, SocketFlags.None, new IPEndPoint(gateway, PcpPort), cts.Token)
                .ConfigureAwait(false);
            var buf = new byte[64];
            var res = await udp.ReceiveFromAsync(buf, SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), cts.Token).ConfigureAwait(false);
            var n = res.ReceivedBytes;
            if (n < 60 || res.RemoteEndPoint is not IPEndPoint rep
                || !rep.Address.Equals(gateway)) return 0;
            // 响应头：version=2、opcode=MAP（低 5 位）、result=0（SUCCESS）
            if (buf[0] != 2 || (buf[1] & 0x1F) != 1 || buf[15] != 0) return 0;
            // nonce 回显校验：确认是对本次请求的应答
            for (var i = 0; i < 12; i++)
                if (buf[24 + i] != nonce[i]) return 0;
            if (buf[36] != 17) return 0;               // protocol = UDP
            if (BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(40)) != localPort) return 0;
            return BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(42));  // assigned external port
        }
        catch
        {
            return 0;                                  // 网关无 PCP 服务/超时/取消——静默降级
        }
    }

    /// <summary>取首个可达网卡的默认网关 IPv4（PCP 服务只发单播到网关）。</summary>
    private static IPAddress? GetDefaultGateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var gw in ni.GetIPProperties().GatewayAddresses)
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        return gw.Address;
            }
        }
        catch { /* 枚举失败按无网关处理 */ }
        return null;
    }
}
