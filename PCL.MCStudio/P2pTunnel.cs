using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using KcpSharp;

namespace PCL.MCStudio;

/// <summary>连接方式：Auto=直连优先中转兜底，DirectOnly=仅直连，RelayOnly=仅中转（frp）。</summary>
public enum P2pMode
{
    Auto,
    DirectOnly,
    RelayOnly,
}

/// <summary>P2P 路由结果类型。</summary>
public enum P2pRouteKind
{
    /// <summary>IPv6 直连（TCP 直通）。</summary>
    V6,
    /// <summary>IPv4 UDP 打洞 + KCP 隧道。</summary>
    UdpHole,
    /// <summary>v50.6.7 relay-first：frp 中转作为首条上游立即就绪（0 等待进服），
    /// 后台打洞打通后升级为直连（下一次 MC 重连生效）。</summary>
    Relay,
}

/// <summary>
/// 本机回环转发器：把任意 TCP 上游（如 frp 中转地址）包成本地 127.0.0.1 监听。
/// 用途：中转线路也不向游戏暴露公网地址——烤入启动参数的永远是 127.0.0.1，
/// 游戏内服务器列表看不到云端中转地址:端口，杜绝绕过启动器手动直连中转。
/// 监听随进程存活（数量少、空转开销可忽略），进程退出即全部释放。
/// </summary>
public static class LoopbackForwarder
{
    private static readonly List<TcpListener> KeepAlive = new();

    /// <summary>开启转发：返回形如 127.0.0.1:{port} 的本地地址，连接会被泵到 targetHost:targetPort。</summary>
    public static string Open(string targetHost, int targetPort)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        lock (KeepAlive) KeepAlive.Add(listener);
        _ = Task.Run(() => AcceptLoopAsync(listener, targetHost, targetPort));
        return $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    private static async Task AcceptLoopAsync(TcpListener listener, string host, int port)
    {
        while (true)
        {
            TcpClient mc;
            try { mc = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch { break; }   // 监听被关闭（进程退出）
            _ = Task.Run(() => ServeAsync(mc, host, port));
        }
    }

    private static async Task ServeAsync(TcpClient mc, string host, int port)
    {
        // v50.6.3：上游连接超时 5s × 3 次重试（间隔 2s）——中转链路抖动/僵尸代理被
        // 云端对账清理的窗口期内撑过 MC 30s 连接超时，而不是立即关游戏连接
        // （此前游戏侧表现为「已建立的连接被您的主机上的软件中止」，误导为杀软问题）。
        // 关键事件落 P2pTrace 并转发启动器主日志（LauncherLog）。
        TcpClient? upstream = null;
        Exception? last = null;
        for (var attempt = 1; attempt <= 3 && upstream is null; attempt++)
        {
            try
            {
                var c = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await c.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                upstream = c;
            }
            catch (Exception ex)
            {
                last = ex;
                P2pTrace.Log($"[中转] 上游 {host}:{port} 第 {attempt}/3 次连接失败：{ex.Message}");
                P2pTunnel.RaiseLauncherLog($"中转上游 {host}:{port} 连接失败（第 {attempt}/3 次）");
                if (attempt < 3)
                {
                    try { await Task.Delay(2000).ConfigureAwait(false); } catch { }
                }
            }
        }
        if (upstream is null)
        {
            P2pTrace.Log($"[中转] 上游 {host}:{port} 3 次重试全部失败，已关闭游戏连接：{last?.Message}");
            P2pTunnel.RaiseLauncherLog($"中转线路 {host}:{port} 暂不可用（3 次重试失败），游戏连接已断开——请稍后重连或让房主确认房间状态");
            try { mc.Close(); } catch { }
            return;
        }
        try
        {
            using var mcSocket = mc;
            using var up = upstream;
            var upStream = up.GetStream();
            var mcStream = mcSocket.GetStream();
            var t1 = PumpAsync(mcStream, upStream);
            var t2 = PumpAsync(upStream, mcStream);
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            P2pTrace.Log($"[中转] 泵异常 {host}:{port}：{ex.Message}");
            try { mc.Close(); } catch { }
        }
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[65536];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                await to.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { to.Close(); } catch { }
        }
    }
}

/// <summary>加入端 P2P 尝试结果：LocalAddress 为 MC 客户端应连接的本地回环地址；null 表示未打通（走中转）。</summary>
public sealed record P2pAttempt(P2pRouteKind Kind, string LocalAddress);

/// <summary>
/// P2P 直连引擎（三级降级链：IPv6 直连 → IPv4 UDP 打洞 + KCP 隧道 → frp 中转兜底）。
/// 云端只做信令（候选交换）与 STUN-lite（回显公网映射），数据面全程点对点。
/// </summary>
public static class P2pTunnel
{
    private const int ConvId = 0x4D435050; // "MCPP"
    private const string MagicPrefix = "MCP2P|";

    /// <summary>v50 M6：房间级握手 magic。secret 非空时以 HMAC-SHA256(secret, 房间码)
    /// 派生（双方从信令各拿到同一 secret，无需预先共享），空则回落旧静态 magic（旧端兼容）。
    /// 房间码统一大写——加入端可能传原始大小写，两侧不统一会导致派生值不一致、打洞静默失败。</summary>
    private static string RoomMagic(string code, string secret)
    {
        var norm = code.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(secret)) return MagicPrefix + norm;
        using var h = new HMACSHA256(Encoding.ASCII.GetBytes(secret));
        var mac = Convert.ToHexString(h.ComputeHash(Encoding.ASCII.GetBytes(MagicPrefix + norm)));
        return MagicPrefix + mac[..16];
    }

    /// <summary>P2P 会话时长上限（P0-G1：原 180min 硬超时会长局被切；默认 8h，
    /// 环境变量 MCSTUDIO_SESSIONHOURS 可覆盖）。</summary>
    private static readonly TimeSpan SessionTimeout = LoadSessionTimeout();

    /// <summary>P2P 会话结束事件（reason 区分"游戏客户端断开 / 时长上限 / 异常断开"；
    /// UI 可订阅用于提示，P2-G2 keep-alive 依赖此处的分类数据）。</summary>
    public static event Action<string>? SessionEnded;

    /// <summary>v50.6.3：隧道关键事件转发到启动器主日志（主程序侧订阅 → ModBase.Log）。
    /// 此前隧道层日志只在 P2pTrace 独立文件里，主日志零痕迹，排障必须两头翻——消除该盲区。</summary>
    public static event Action<string>? LauncherLog;

    public static void RaiseLauncherLog(string message)
    {
        try { LauncherLog?.Invoke(message); } catch { /* 订阅方异常不外溢 */ }
    }

    private static void RaiseSession(string reason)
    {
        try { SessionEnded?.Invoke(reason); } catch { /* 订阅方异常不外溢 */ }
    }

    private static TimeSpan LoadSessionTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("MCSTUDIO_SESSIONHOURS");
        var hours = double.TryParse(raw, out var h) && h > 0 && h <= 24 ? h : 8;
        return TimeSpan.FromHours(hours);
    }

    /// <summary>云机 STUN-lite 端口（默认 8802/8803，可用环境变量 MCSTUDIO_STUNPORTS 覆盖，如本地测试）。</summary>
    private static readonly int[] StunPorts =
        (Environment.GetEnvironmentVariable("MCSTUDIO_STUNPORTS") ?? "8802,8803")
        .Split(',').Select(s => int.TryParse(s.Trim(), out var p) ? p : 0).Where(p => p > 0).ToArray();
    /// <summary>公共 STUN 开关（回环测试置 MCSTUDIO_PUBSTUN=0 跳过，避免公网映射污染端口保持性判定）。</summary>
    private static readonly bool UsePublicStun = Environment.GetEnvironmentVariable("MCSTUDIO_PUBSTUN") != "0";

    /// <summary>v49.4 打洞 socket 池大小（默认 12，MCSTUDIO_PUNCHSOCKETS 可覆盖 2-24）。
    /// 每个 socket 独立 NAT 映射：cone 侧=12 个稳定可直达端口（对端直达任一即胜），
    /// 对称侧=12 路端口预留在对端扫描窗内，命中面较 v49 双 socket 大幅提升。</summary>
    private static readonly int PunchSocketCount = LoadPunchSocketCount();

    private static int LoadPunchSocketCount()
    {
        var raw = Environment.GetEnvironmentVariable("MCSTUDIO_PUNCHSOCKETS");
        return int.TryParse(raw, out var n) && n >= 2 && n <= 24 ? n : 12;
    }

    /// <summary>SIO_UDP_CONNRESET：Windows 上对端端口未开时 ICMP 不可达会把后续
    /// ReceiveFrom 变成 10054 异常——打洞期间大量发往「猜测端口」，必须关闭。</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>新建打洞 UDP socket：关闭 CONNRESET + 绑定临时端口。</summary>
    private static Socket NewUdpSocket()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { s.IOControl(SioUdpConnReset, new byte[4], null); } catch { /* 非 Windows 忽略 */ }
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return s;
    }

    /// <summary>按池大小新建打洞 socket 池（任一创建失败则返回已建好的部分）。</summary>
    private static List<Socket> CreatePunchPool()
    {
        var list = new List<Socket>(PunchSocketCount);
        for (var i = 0; i < PunchSocketCount; i++)
        {
            try { list.Add(NewUdpSocket()); }
            catch { break; }   // 系统端口耗尽等：用已建好的部分
        }
        if (list.Count == 0) list.Add(NewUdpSocket());   // 兜底重试一次
        return list;
    }

    /// <summary>v49.4 KCP 游戏流量调优：更新间隔 100→20ms（MC 交互延迟），
    /// 快速重传 2（丢包快速恢复，移动网关键），关闭 KCP 拥塞控制（MC 协议自带
    /// TCP 流控，双层 CC 在丢包链路上会叠加降速），窗口 256→512 吸收区块突发。</summary>
    private static KcpConversationOptions GameKcpOptions() => new()
    {
        Mtu = 1200,
        StreamMode = true,
        NoDelay = true,
        UpdateInterval = 20,
        FastResend = 2,
        DisableCongestionControl = true,
        SendWindow = 512,
        ReceiveWindow = 512,
        RemoteReceiveWindow = 512,
        SendQueueSize = 256,
        ReceiveQueueSize = 256,
    };

    // ---------------------------------------------------------------- 加入端

    /// <summary>
    /// 加入端 P2P 尝试（v50.6.7 relay-first）：中继上游秒级就绪（0 等待进服）→ 后台继续
    /// 完整打洞流程（候选交换 → IPv6 直连 → UDP 打洞），打通后把上游从中继升级为直连
    /// （下一次 MC 重连生效，不断当前连接）。relayAddr 为空或中继不可达时退回同步打洞；
    /// 打洞也失败才返回 null（调用方走 LoopbackForwarder 兜底）。
    /// </summary>
    public static async Task<P2pAttempt?> JoinerTryAsync(StudioApiClient api, string token, string code,
        string? relayAddr, IProgress<string>? log, CancellationToken ct)
    {
        var relay = new JoinerRelay(api, token, code, log);
        // v50.6.7 relay-first：先建中继上游（秒级）——游戏即刻可玩，打洞不再阻塞进服
        if (!string.IsNullOrWhiteSpace(relayAddr) && IPEndPoint.TryParse(relayAddr, out var rep))
        {
            try
            {
                var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                await tcp.ConnectAsync(rep.Address, rep.Port, cts.Token).ConfigureAwait(false);
                relay.StartWith(new RelayUpstream(tcp));
                relay.UpgradeInBackgroundAsync();
                log?.Report("中继线路已就绪（后台继续尝试直连升级，打通后延迟更低）");
                return new P2pAttempt(P2pRouteKind.Relay, relay.LocalAddress);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Report("中继线路不可用（" + ex.Message + "），转入直连尝试…");
            }
        }
        var first = await relay.ConnectUpstreamAsync(ct).ConfigureAwait(false);
        if (first is null)
        {
            relay.Dispose();
            return null;
        }
        relay.StartWith(first);
        return new P2pAttempt(first.Kind, relay.LocalAddress);
    }

    /// <summary>P2P 上游抽象：稳定本地监听背后的实际隧道（v6 TCP 或 KCP）。</summary>
    private abstract class P2pUpstream : IDisposable
    {
        public abstract P2pRouteKind Kind { get; }
        /// <summary>上游当前是否可用。</summary>
        public abstract bool Alive { get; }
        /// <summary>把 MC 客户端 TCP 双向泵到上游。返回 true=MC 侧先断开（正常结束），
        /// false=上游异常断开（调用方负责释放 mc 并触发重连）。</summary>
        public abstract Task<bool> PumpAsync(TcpClient mc, CancellationToken ct);
        public abstract void Dispose();
    }

    /// <summary>IPv6 直连上游（纯 TCP，无需 keep-alive）。</summary>
    private sealed class V6Upstream : P2pUpstream
    {
        private readonly TcpClient _remote;
        public V6Upstream(TcpClient remote) => _remote = remote;
        public override P2pRouteKind Kind => P2pRouteKind.V6;
        public override bool Alive => _remote.Connected;
        public override async Task<bool> PumpAsync(TcpClient mc, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var t1 = CopyStreamToStream(mc.GetStream(), _remote.GetStream(), cts.Token);
            var t2 = CopyStreamToStream(_remote.GetStream(), mc.GetStream(), cts.Token);
            var finished = await Task.WhenAny(t1, t2).ConfigureAwait(false);
            return finished == t1;   // 本地→远端先结束 = MC 侧断开
        }
        public override void Dispose() => _remote.Dispose();
    }

    /// <summary>v50.6.7 relay-first 中继上游：到云端 frp 中转地址的 TCP（与打洞上游同一抽象，
    /// 可作为 JoinerRelay 的首条上游——游戏 0 等待进服，后台打洞打通后升级直连）。</summary>
    private sealed class RelayUpstream : P2pUpstream
    {
        private readonly TcpClient _remote;
        public RelayUpstream(TcpClient remote) => _remote = remote;
        public override P2pRouteKind Kind => P2pRouteKind.Relay;
        public override bool Alive => _remote.Connected;
        public override async Task<bool> PumpAsync(TcpClient mc, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var t1 = CopyStreamToStream(mc.GetStream(), _remote.GetStream(), cts.Token);
            var t2 = CopyStreamToStream(_remote.GetStream(), mc.GetStream(), cts.Token);
            var finished = await Task.WhenAny(t1, t2).ConfigureAwait(false);
            return finished == t1;
        }
        public override void Dispose() => _remote.Dispose();
    }

    /// <summary>KCP 打洞上游：同 socket 会话 + 15s 裸 magic keep-alive（保持本端 NAT 映射；
    /// 移动 CGNAT UDP 映射超时常见 30-60s，v49 的 20s 偏紧，收窄到 15s；
    /// 对端 KcpSharp 对 conv 不匹配的数据报双层静默丢弃，已实测验证）。</summary>
    private sealed class KcpUpstream : P2pUpstream
    {
        private readonly Socket _udp;
        private readonly IPEndPoint _observed;
        private readonly IKcpTransport<KcpConversation> _transport;
        private readonly KcpConversation _conv;
        private readonly CancellationTokenSource _life = new();
        private volatile bool _dead;

        public KcpUpstream(Socket udp, IPEndPoint observed)
        {
            _udp = udp;
            _observed = observed;
            _transport = KcpSocketTransport.CreateConversation(udp, observed, ConvId, GameKcpOptions());
            _transport.Start();
            _conv = _transport.Connection;
            _ = Task.Run(KeepAliveLoopAsync);
        }

        public override P2pRouteKind Kind => P2pRouteKind.UdpHole;
        public override bool Alive => !_dead;

        private async Task KeepAliveLoopAsync()
        {
            // 裸 magic（"MCP2" 开头，与 KCP conv "MCPP" 不同）；对端 KcpSharp 按会话 ID 静默丢弃，
            // 但足以让两端 NAT 刷新各自的出站映射，防止游戏静默期（加载/挂机）映射过期
            var magic = Encoding.ASCII.GetBytes(MagicPrefix + "KA");
            while (!_life.IsCancellationRequested && !_dead)
            {
                try { _udp.SendTo(magic, _observed); }
                catch { _dead = true; break; }
                try { await Task.Delay(15000, _life.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        public override async Task<bool> PumpAsync(TcpClient mc, CancellationToken ct)
        {
            try
            {
                var t1 = PumpTcpToKcp(mc, _conv, ct);
                var t2 = PumpKcpToTcp(_conv, mc, ct);
                var finished = await Task.WhenAny(t1, t2).ConfigureAwait(false);
                var upstreamSide = finished == t2;   // 上游→MC 泵先结束 = 上游断了
                if (upstreamSide) _dead = true;
                return !upstreamSide;
            }
            catch
            {
                _dead = true;
                return false;
            }
        }

        public override void Dispose()
        {
            _life.Cancel();
            try { _transport.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 加入端稳定中继（P2-G2）：固定 127.0.0.1 监听地址贯穿整个会话（≤SessionTimeout）；
    /// 隧道断开自动重建（保留 LAN/v6/打洞全链路），MC 客户端断开后监听保持待命，
    /// 用户在 MC 的「重新连接」直接进入，隧道未就绪则先重建再接入。
    /// </summary>
    private sealed class JoinerRelay : IDisposable
    {
        private readonly StudioApiClient _api;
        private readonly string _token;
        private readonly string _code;
        private readonly IProgress<string>? _log;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _reconnectLock = new(1, 1);   // 防止并发重建出多条隧道
        private P2pUpstream? _current;

        public string LocalAddress { get; }

        public JoinerRelay(StudioApiClient api, string token, string code, IProgress<string>? log)
        {
            _api = api;
            _token = token;
            _code = code;
            _log = log;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            LocalAddress = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        }

        private void Log(string message)
        {
            _log?.Report(message);
            P2pTrace.Log(message);   // v46.2 复盘日志：UI 进度同步落盘
        }

        /// <summary>接入首条上游后启动监听循环与会话总时长控制。</summary>
        public void StartWith(P2pUpstream first)
        {
            _current = first;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SessionTimeout, _cts.Token).ConfigureAwait(false);
                    RaiseSession($"UDP 直连会话达到时长上限（{SessionTimeout.TotalHours:0.#} 小时）自动关闭");
                    Dispose();
                }
                catch (OperationCanceledException) { }
            });
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient mc;
                try { mc = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { break; }
                _ = Task.Run(() => ServeMcAsync(mc, ct), ct);
            }
        }

        private async Task ServeMcAsync(TcpClient mc, CancellationToken ct)
        {
            try
            {
                var up = _current;
                if (up is null || !up.Alive)
                {
                    Log("P2P 隧道已断开，正在重建…");
                    RaiseLauncherLog("P2P 直连隧道已断开，正在重建…");
                    up = await ReconnectAsync(ct).ConfigureAwait(false);
                    if (up is null)
                    {
                        // v50.6.3：不再 Dispose 整个 relay——保留本地监听 + 60s 周期后台重试，
                        // MC 里「重新连接」始终有救；只有 8h 会话上限才真正关闭
                        Log("本轮重连失败（房间可能已关闭），60s 后自动再试；在 MC 里点「重新连接」即可");
                        RaiseLauncherLog("P2P 隧道重连失败，60 秒后自动重试（MC 里点「重新连接」即可）");
                        RaiseSession("P2P 隧道断开且重连失败（持续重试中）");
                        StartBackgroundRetry();
                        return;
                    }
                    RaiseLauncherLog("P2P 直连隧道已重建");
                }
                Log("游戏客户端已接入隧道");
                var mcClosed = await up.PumpAsync(mc, ct).ConfigureAwait(false);
                // v50.6.7：pump 期间 _current 已被升级/重建为其他上游 → 退役本旧上游即可，
                // MC 重连将直接接入更优线路（中继→直连升级后，旧中继连接由此回收）
                if (!ReferenceEquals(_current, up) && _current is { Alive: true })
                {
                    try { up.Dispose(); } catch { }
                    Log("线路已升级/重建，游戏重连时将使用更优线路");
                    return;
                }
                if (mcClosed)
                {
                    Log("游戏客户端断开，隧道保持待命（在 MC 里点「重新连接」即可返回）");
                }
                else
                {
                    // 上游异常断开：立即后台重建，用户在 MC 里点「重新连接」时通常已就绪
                    _current = null;
                    try { up.Dispose(); } catch { }
                    Log("P2P 隧道异常断开，自动重建中…（重建完成后在 MC 里点「重新连接」即可）");
                    RaiseLauncherLog("P2P 直连线路断开，正在自动重建…");
                    _ = Task.Run(async () =>
                    {
                        var fresh = await ReconnectAsync(_cts.Token).ConfigureAwait(false);
                        if (fresh is null)
                        {
                            Log("隧道重建失败，60s 后自动再试；请稍后在 MC 里点「重新连接」");
                            RaiseLauncherLog("P2P 隧道重建失败，60 秒后自动重试");
                            StartBackgroundRetry();
                        }
                        else
                        {
                            Log("隧道已重建，在 MC 里点「重新连接」即可返回游戏");
                            RaiseLauncherLog("P2P 隧道已重建，可在 MC 里点「重新连接」返回游戏");
                        }
                    }, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log("隧道会话异常：" + ex.Message);
                RaiseLauncherLog("隧道会话异常：" + ex.Message);
            }
            finally
            {
                try { mc.Dispose(); } catch { }
            }
        }

        /// <summary>v50.6.3：后台周期重试（60s 间隔，直至成功或 relay 被销毁）。
        /// 成功后 _current 已就绪，MC 里点「重新连接」即直接接入。</summary>
        private void StartBackgroundRetry()
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested && _current is null)
                {
                    try { await Task.Delay(60000, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    Log("后台自动重试中…");
                    var up = await ReconnectAsync(_cts.Token).ConfigureAwait(false);
                    if (up is not null)
                    {
                        Log("隧道已自动重建，在 MC 里点「重新连接」即可返回游戏");
                        RaiseLauncherLog("P2P 隧道已自动重建，可在 MC 里点「重新连接」返回游戏");
                        return;
                    }
                }
            }, CancellationToken.None);
        }

        /// <summary>v50.6.7 relay-first 后台升级：中继已供游戏游玩，后台继续完整打洞流程
        /// （信令→LAN→v6→UDP），打通后把 _current 从中继换成直连——下一次 MC 重连生效，
        /// 不断当前连接（MC 是 TCP 流，跨传输热切换会丢字节，故不热切）。与 ReconnectAsync
        /// 共用锁避免并发开两条路；若期间断线重建已建立直连，本次结果直接丢弃。</summary>
        public void UpgradeInBackgroundAsync()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var up = await ConnectUpstreamAsync(_cts.Token).ConfigureAwait(false);
                    if (up is null)
                    {
                        Log("后台直连尝试未成功（保持中继线路，延迟较高）");
                        return;
                    }
                    await _reconnectLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        if (_current is { Alive: true } && _current is not RelayUpstream)
                        {
                            up.Dispose();   // 竞速胜出的已是直连（断线重建等），丢弃本次结果
                            return;
                        }
                        _current = up;
                        Log("直连已打通（延迟更低）——在 MC 里断开重连一次即可切换直连线路");
                        RaiseLauncherLog("P2P 直连已打通，MC 里重新连接即可切换直连线路");
                    }
                    finally { _reconnectLock.Release(); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log("后台直连升级异常：" + ex.Message); }
            }, CancellationToken.None);
        }

        /// <summary>重建隧道（最多 3 次，间隔 3s；复用完整 joiner 流程：信令→LAN→v6→打洞）。
        /// 加锁串行化：后台自动重建与 MC 重连触发的重建不会并发开两条路。</summary>
        private async Task<P2pUpstream?> ReconnectAsync(CancellationToken ct)
        {
            await _reconnectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 等锁期间别人已重建成功 → 直接复用
                if (_current is { Alive: true }) return _current;
                for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
                {
                    if (i > 0)
                    {
                        try { await Task.Delay(3000, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return null; }
                    }
                    try
                    {
                        var up = await ConnectUpstreamAsync(ct).ConfigureAwait(false);
                        if (up is not null)
                        {
                            _current = up;
                            return up;
                        }
                    }
                    catch (OperationCanceledException) { return null; }
                    catch (Exception ex)
                    {
                        Log("重连尝试失败：" + ex.Message);
                    }
                }
                return null;
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        /// <summary>建立一条到房主的新上游（原 JoinerTryAsync 主体）。</summary>
        public async Task<P2pUpstream?> ConnectUpstreamAsync(CancellationToken ct)
        {
            var api = _api;
            var token = _token;
            var code = _code;
            var apiHost = api.ApiHost;
            var v6 = CollectV6();
            var lan = CollectV4Lan();
            P2pTrace.Session("joiner", code, $"候选 v6×{v6.Count} lan×{lan.Count}（{apiHost}）");
            Log($"候选收集：v6×{v6.Count} lan×{lan.Count}");

            var sw = Stopwatch.StartNew();      // 全程计时（埋点用）
            var punchSw = new Stopwatch();      // 打洞阶段计时
            // 1) v49.4 打洞 socket 池：每 socket 独立映射，先并发探测 NAT（含每 socket 映射 + 步长）
            var pool = CreatePunchPool();
            var udp = pool[0];
            var punchPort = ((IPEndPoint)udp.LocalEndPoint!).Port;
            P2pTrace.Log($"打洞 socket 池 ×{pool.Count}（首口 {punchPort}），开始 NAT 探测…");
            var probe = await Task.Run(() => ProbeNatPool(pool, apiHost, ct), ct).ConfigureAwait(false);
            var mapped = probe.Mapped;
            var preserving = probe.Preserving;
            var natSelf = preserving ? "cone" : "symmetric";
            var predPorts = probe.PredPorts;   // 兼容字段：本端下一次映射的预测窗口（cone 为空）
            P2pTrace.Log(probe.Samples.Count > 0
                ? $"NAT 样本：[{string.Join(" | ", probe.Samples)}] → 判定 {natSelf}，映射 {mapped}，delta {probe.Delta}"
                : "NAT 样本：无任何 STUN 应答（探测目标全部不可达），按 cone 处理但判定不可信");
            Log(predPorts.Count > 0
                ? $"NAT 探测：{natSelf}（映射 {mapped}，步长 {probe.Delta}，预测窗口 ×{predPorts.Count}）"
                : $"NAT 探测：{natSelf}（映射 {mapped}）");
            if (probe.Maps.Count > 0)
                P2pTrace.Log($"每 socket 映射：[{string.Join(" | ", probe.Maps)}]");
            // v50.6.6：映射保活——探测到开窗全程无流量，CGN UDP 超时 15–30s 会先杀映射
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var natKeepalive = Task.Run(() => KeepNatMappingsAsync(pool, apiHost, kaCts.Token), CancellationToken.None);
            var mapTask = NatPortMapper.MapAsync(udp, ct);   // P1-D：UPnP/PMP/PCP 主动映射（并行，不阻塞信令）
            var upnpPort = 0;
            var upnpLogged = false;
            P2pPeer? host = null;
            var v6Tried = false;   // v50.6.5：声明提前供 Report 闭包读取（归因 v6 路径）

            // 埋点：连接终点上报一次（后台尽力，失败静默）
            void Report(string result)
            {
                kaCts.Cancel();   // v50.6.6：所有终点（含异常后 Report）统一停保活
                P2pTrace.End("joiner", result,
                    $"总耗时 {sw.ElapsedMilliseconds}ms natSelf={natSelf} natPeer={host?.Nat ?? "unknown"}");
                var payload = new
                {
                    role = "joiner",
                    nat_self = natSelf,
                    nat_peer = host?.Nat ?? "unknown",
                    result,
                    punch_ms = punchSw.IsRunning || punchSw.ElapsedMilliseconds > 0 ? (int)punchSw.ElapsedMilliseconds : 0,
                    total_ms = (int)sw.ElapsedMilliseconds,
                    v6_count = v6.Count,
                    v6_tried = v6Tried,
                    lan_count = lan.Count,
                    pred_count = host?.PredPorts?.Count ?? 0,
                    upnp = mapTask.IsCompleted && mapTask.Result > 0 ? 1 : 0,
                    sock = pool.Count,
                    maps = probe.Maps.Count,
                };
                _ = Task.Run(async () =>
                {
                    try { await api.P2pMetricAsync(token, payload, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* 埋点失败不影响主流程 */ }
                }, CancellationToken.None);
            }

            // 2) 轮询：上报候选并拉取房主候选（通用候选先试 v6；定向候选到位才打洞）
            var lastSrvNow = 0.0;
            var secret = "";   // v50 M6：双方 ≥50 时云端下发，HMAC 化握手 magic
            for (var attempt = 0; attempt < 12 && !ct.IsCancellationRequested; attempt++)
            {
                var signal = await api.P2pSignalAsync(token, code, "joiner",
                    new P2pCandidates(v6, mapped, natSelf, 0, lan, punchPort, predPorts, upnpPort,
                        pool.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToList(), probe.MapsStrings, probe.Delta),
                    ct).ConfigureAwait(false);
                if (signal.Secret.Length > 0) secret = signal.Secret;
                upnpPort = mapTask.IsCompleted ? mapTask.Result : 0;
                if (upnpPort > 0 && !upnpLogged)
                {
                    upnpLogged = true;
                    Log($"路由器端口映射成功（外部 {upnpPort}），已随候选上报");
                }
                lastSrvNow = signal.SrvNow;
                host = signal.Peer;
                if (host is not null)
                {
                    // IPv6 直连尽早尝试（零打洞，host 的 v6 地址与定向路相同）
                    if (!v6Tried && host.V6.Count > 0 && host.Port > 0)
                    {
                        v6Tried = true;
                        Log($"尝试 IPv6 直连（{host.V6.Count} 个候选）…");
                        var v6Attempt = await TryV6DirectAsync(host.V6, host.Port, ct).ConfigureAwait(false);
                        if (v6Attempt is not null)
                        {
                            Log($"IPv6 直连成功：{v6Attempt}");
                            // 通知房主本端已走 v6，免其继续为该路打洞（v4 置空 + 标记）
                            try { await api.P2pSignalAsync(token, code, "joiner",
                                new P2pCandidates(v6, "", "direct-v6", 0), ct).ConfigureAwait(false); } catch { /* 尽力通知 */ }
                            Report("v6");
                            DisposeExcept(pool, null);
                            return new V6Upstream(v6Attempt);
                        }
                        Log("IPv6 直连未成功，继续 P2P 打洞");
                    }
                    // Targeted=True：host 已为该加入者开出独立打洞路，可开始打洞
                    if (host.Targeted) break;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            if (host is null)
            {
                Log("房主候选不可用（12 次轮询未拿到房主信令，房主可能未开房主服务或信令异常）");
                Report("no_peer");
                DisposeExcept(pool, null);
                return null;
            }

            // 3) LAN 优先：同网段直连（宿舍/家庭双机 0 打洞；AP 隔离时 1~2s 内失败自然落回公网）
            var magic = RoomMagic(code, secret);
            if (host.Targeted && host.PunchPort > 0)
            {
                foreach (var ip in host.V4Lan ?? (IReadOnlyList<string>)Array.Empty<string>())
                {
                    if (!IPAddress.TryParse(ip, out var lanIp)) continue;
                    var lanEp = new IPEndPoint(lanIp, host.PunchPort);
                    Log($"尝试同网段直连（{lanEp}）…");
                    punchSw.Start();
                    var lanObserved = await Task.Run(() => HolePunch(udp, new[] { lanEp }, magic + "|J|",
                        magic + "|", 1500, ct), ct).ConfigureAwait(false);
                    if (lanObserved is not null)
                    {
                        Log($"同网段直连成功（对端 {lanObserved}），建立 KCP 隧道…");
                        Report("lan");
                        DisposeExcept(pool, udp);
                        return new KcpUpstream(udp, lanObserved);
                    }
                }
                if ((host.V4Lan?.Count ?? 0) > 0) Log("同网段直连未成功，继续公网打洞");
            }

            // 4) IPv4 UDP 打洞（v49.4 池化引擎）：组合矩阵同 v49（仅双侧对称且完全不可预测才放弃），
            //    但「可预测」的判据扩展为：任一侧有预测窗口 / 每 socket 映射 / 步长 / UPnP 双侧映射——
            //    池化打洞下对端每 socket 端口已直接可达（cone）或落在扩展扫描窗内（对称）。
            var peerSymmetric = string.Equals(host.Nat, "symmetric", StringComparison.OrdinalIgnoreCase);
            var dualSymmetric = !preserving && peerSymmetric;
            var anyPrediction = (host.PredPorts?.Count ?? 0) > 0 || (host.Maps?.Count ?? 0) > 0
                || host.Delta > 0 || predPorts.Count > 0 || probe.Maps.Count > 0
                || (host.UpnpPort > 0 && upnpPort > 0);   // 双方都有外部映射 = 双向可入站
            if (!host.Targeted || string.IsNullOrEmpty(host.V4) || !IPEndPoint.TryParse(host.V4, out var hostEp)
                || (dualSymmetric && !anyPrediction))
            {
                Log(dualSymmetric && !anyPrediction
                    ? $"双侧均为对称 NAT 且端口步长完全不可预测，跳过打洞（本端 {natSelf} / 对端 {host.Nat}）"
                    : $"打洞条件不满足（Targeted={host.Targeted}，房主 V4='{host.V4}'，房主未就绪打洞路）");
                Report("skipped");
                DisposeExcept(pool, null);
                return null;
            }
            // P1-F 同步起打：双方以服务器锚定的同一绝对时刻开始发 magic（首包命中率↑、窗口全重叠）
            if (await WaitForRendezvousAsync(lastSrvNow, host.PunchAt, ct).ConfigureAwait(false))
                Log("已与房主同步起打");
            // v49.4 窗口：cone-cone 12s（引擎通常 <2s 命中，留余量）；涉对称 10s；
            // 双侧对称且无步长（彩票局）7s 尽快回落中转
            var punchTimeout = dualSymmetric && !anyPrediction ? 7000
                : !preserving || peerSymmetric ? 10000 : 12000;
            var targets = BuildPunchTargets(host, hostEp, peerSymmetric || !preserving ? 12 : 8);
            Log($"UDP 打洞中（{pool.Count} 路并发 × 目标 {targets.Count}（含 {hostEp}"
                + (host.UpnpPort > 0 ? "+外部映射" : "") + $"），{punchTimeout / 1000}s 窗口）…");
            P2pTrace.Log($"打洞目标组（{targets.Count}）：[{string.Join(" | ", targets)}]");
            punchSw.Start();
            var win = await PunchPoolAsync(pool, targets, magic + "|J|",
                magic + "|", punchTimeout, ct).ConfigureAwait(false);
            if (win is null)
            {
                Log("UDP 打洞未成功（窗口内未收到房主任何 magic 应答）");
                Report("timeout");
                DisposeExcept(pool, null);
                return null;
            }
            Log($"UDP 打洞成功（对端 {win.Observed}），建立 KCP 隧道…");
            Report("udp");
            DisposeExcept(pool, win.Socket);
            return new KcpUpstream(win.Socket, win.Observed);
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            _cts.Cancel();
            try { _current?.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- 房主端

    /// <summary>
    /// 房主端 P2P 服务（多人）：主循环上报通用候选并发现全部加入者，
    /// 每个加入者开一条独立路线（独立 UDP socket 探测 → 定向上报 → 双向打洞
    /// → KCP 收首包后懒连接本地 MC 服务器并双向泵）。Dispose 停止全部后台活动。
    /// </summary>
    public sealed class HostService : IDisposable
    {
        /// <summary>同时打洞的 P2P 路上限；超出自动走中转线路。</summary>
        public const int MaxRoutes = 8;

        private readonly StudioApiClient _api;
        private readonly string _token;
        private readonly string _code;
        private readonly int _mcPort;
        private readonly Func<int>? _portProvider;   // v48 虚拟局域网：桥接端口动态热取

        /// <summary>当前桥接目标端口（LAN 模式随「对局域网开放」变化；服务端模式恒为 _mcPort）。</summary>
        private int ResolvePort() => _portProvider?.Invoke() ?? _mcPort;
        private readonly CancellationTokenSource _cts = new();
        private Socket? _udp;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _active =
            new(StringComparer.OrdinalIgnoreCase);   // 打洞中的路（主循环与路线任务并发访问）
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _failed =
            new(StringComparer.OrdinalIgnoreCase);   // 失败记录（3 分钟内不重试）
        private Task? _loop;

        /// <summary>房主端 P2P 活动日志（线程池线程触发，订阅方自行调度）。</summary>
        public event Action<string>? Log;

        private void LogLine(string message)
        {
            Log?.Invoke(message);
            P2pTrace.Log(message);   // v46.2 复盘日志：房主端全部活动同步落盘
        }

        public HostService(StudioApiClient api, string token, string code, int mcPort,
            Func<int>? portProvider = null)
        {
            _api = api;
            _token = token;
            _code = code;
            _mcPort = mcPort;
            _portProvider = portProvider;   // v48 虚拟局域网：桥接目标端口动态热取（服务端模式传 null）
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                var v6 = CollectV6();
                var lan = CollectV4Lan();
                var udp = NewUdpSocket();
                _udp = udp;
                var probe = await Task.Run(() => ProbeNatPool(new[] { udp }, _api.ApiHost, ct), ct).ConfigureAwait(false);
                var nat = probe.Preserving ? "cone" : "symmetric";
                LogLine($"[P2P] 房主上线：NAT={nat} 映射={probe.Mapped} v6×{v6.Count} lan×{lan.Count} mcPort={ResolvePort()}（多人直连就绪）");
                // v50.6.5：对称 NAT 提示（手机热点/蜂窝网络的 CGNAT 几乎都是对称型）——
                // 此场景 IPv4 打洞成功率天然极低；有 v6 候选时朋友端会优先走 v6 直连（免打洞）
                if (nat == "symmetric")
                    LogLine(v6.Count > 0
                        ? "[P2P] 提示：本机为对称 NAT（手机热点/蜂窝网络典型特征），IPv4 打洞成功率低——已上报 IPv6 候选，朋友端将优先尝试 v6 直连"
                        : "[P2P] 提示：本机为对称 NAT（手机热点/蜂窝网络典型特征）且未检测到 IPv6——IPv4 打洞成功率低，建议确认手机热点已开启 IPv6 或改用宽带网络");

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var signal = await _api.P2pSignalAsync(_token, _code, "host",
                            new P2pCandidates(v6, probe.Mapped, nat, ResolvePort(), lan), ct).ConfigureAwait(false);
                        foreach (var peer in signal.Peers ?? Array.Empty<P2pPeer>())
                        {
                            if (peer.Email.Length == 0 || _active.ContainsKey(peer.Email)) continue;
                            if (_failed.TryGetValue(peer.Email, out var t))
                            {
                                if ((DateTime.UtcNow - t).TotalMinutes < 3) continue;
                                _failed.TryRemove(peer.Email, out _); // 超过冷却，允许重试（加入者可能已重连）
                            }
                            if (_active.Count >= MaxRoutes)
                            {
                                LogLine($"[P2P] 直连路已达上限 {MaxRoutes}，{peer.Email} 自动走中转");
                                continue;
                            }
                            _active.TryAdd(peer.Email, 1);
                            _ = Task.Run(() => ServeRouteAsync(peer, v6, lan, ct), ct);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogLine("[P2P] 房主循环异常：" + ex.Message);
                    }
                    // v49.4：3s → 1.5s——加入端等待房主定向候选的时间减半（打洞总时延的关键项）
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogLine("[P2P] 房主服务退出：" + ex.Message);
            }
        }

        /// <summary>单个加入者一路：独立 socket 池探测 → 定向上报 → LAN/公网打洞 → KCP 会话（等待其结束）。
        /// v49.4：每路改为多 socket 池（默认 12 路），组合矩阵同 v49（仅双侧对称 NAT 且完全不可预测才放弃）。</summary>
        private async Task ServeRouteAsync(P2pPeer joiner, List<string> v6, List<string> lan, CancellationToken ct)
        {
            var email = joiner.Email;
            var natSelf = "unknown";
            var punchSw = new Stopwatch();
            var upnpPort = 0;   // P1-D 外部映射端口（0=无；Report 闭包也读它，故提前声明）
            var pool = CreatePunchPool();
            var udp = pool[0];
            // v50.6.6：映射保活——路线从建立到开窗可达数十秒，CGN UDP 超时 15–30s 会先杀映射
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var natKeepalive = Task.Run(() => KeepNatMappingsAsync(pool, _api.ApiHost, kaCts.Token), CancellationToken.None);
            NatProbe? probe = null;   // 提前声明供 Report 闭包读取（探测早退时为 null）

            // 埋点：路线终点上报（后台尽力，失败静默）
            void Report(string result)
            {
                kaCts.Cancel();   // v50.6.6：所有终点统一停保活
                P2pTrace.End("host", result,
                    $"{email} 打洞耗时 {punchSw.ElapsedMilliseconds}ms natSelf={natSelf} natPeer={joiner.Nat}");
                var payload = new
                {
                    role = "host",
                    nat_self = natSelf,
                    nat_peer = joiner.Nat,
                    result,
                    punch_ms = punchSw.ElapsedMilliseconds > 0 ? (int)punchSw.ElapsedMilliseconds : 0,
                    v6_count = v6.Count,
                    lan_count = lan.Count,
                    pred_count = joiner.PredPorts?.Count ?? 0,
                    upnp = upnpPort > 0 ? 1 : 0,
                    sock = pool.Count,
                    maps = probe?.Maps.Count ?? 0,
                };
                _ = Task.Run(async () =>
                {
                    try { await _api.P2pMetricAsync(_token, payload, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* 埋点失败不影响主流程 */ }
                }, CancellationToken.None);
            }

            try
            {
                var punchPort = ((IPEndPoint)udp.LocalEndPoint).Port;
                var mapTask = NatPortMapper.MapAsync(udp, ct);   // P1-D：主动映射（与探测并行）
                probe = await Task.Run(() => ProbeNatPool(pool, _api.ApiHost, ct), ct).ConfigureAwait(false);
                var mapped = probe.Mapped;
                var preserving = probe.Preserving;
                natSelf = preserving ? "cone" : "symmetric";
                var predPorts = probe.PredPorts;   // P1-C：本端下一次映射的端口预测（cone 为空）
                // 等映射结果（≤5s， discovery 4s 自限；未完成按 0 上报，不拖信令）
                await Task.WhenAny(mapTask, Task.Delay(5000, CancellationToken.None)).ConfigureAwait(false);
                upnpPort = mapTask.IsCompleted ? mapTask.Result : 0;
                var peerSymmetric = string.Equals(joiner.Nat, "symmetric", StringComparison.OrdinalIgnoreCase);


                // v49.4 组合矩阵：仅"双侧对称且完全不可预测"放弃；可预测判据扩展为
                // 任一侧有预测窗口 / 每 socket 映射 / 步长 / 双侧外部映射
                var anyPrediction = (joiner.PredPorts?.Count ?? 0) > 0 || (joiner.Maps?.Count ?? 0) > 0
                    || joiner.Delta > 0 || predPorts.Count > 0 || probe.Maps.Count > 0
                    || (joiner.UpnpPort > 0 && upnpPort > 0);
                if (!preserving && peerSymmetric && !anyPrediction)
                {
                    LogLine($"[P2P] {email}：双侧均为对称 NAT 且步长不可预测，放弃打洞（走中转）");
                    _failed[email] = DateTime.UtcNow;
                    Report("skipped");
                    return;
                }
                LogLine($"[P2P] 为 {email} 开直连路（映射 {mapped}，本端 {natSelf}，socket ×{pool.Count}"
                    + (predPorts.Count > 0 ? $"，预测窗口 ×{predPorts.Count}" : "")
                    + (upnpPort > 0 ? $"，外部映射 {upnpPort}" : "") + $"），定向上报…");

                IPEndPoint? joinerEp = null;
                var peerPunchAt = 0.0;
                var lastSrvNow = 0.0;
                var secret = "";   // v50 M6：双方 ≥50 时云端下发，HMAC 化握手 magic
                for (var i = 0; i < 6 && !ct.IsCancellationRequested; i++)
                {
                    var signal = await _api.P2pSignalAsync(_token, _code, "host",
                        new P2pCandidates(v6, mapped, natSelf, 0, lan, punchPort, predPorts, upnpPort,
                            pool.Select(s => ((IPEndPoint)s.LocalEndPoint!).Port).ToList(), probe.MapsStrings, probe.Delta),
                        ct, forPeer: email).ConfigureAwait(false);
                    if (signal.Secret.Length > 0) secret = signal.Secret;
                    lastSrvNow = signal.SrvNow;
                    var p = signal.Peer;
                    if (p is not null)
                    {
                        // 加入端已走 v6 直连：免打洞，本路关闭
                        if (p.Nat == "direct-v6" || string.IsNullOrEmpty(p.V4))
                        {
                            LogLine($"[P2P] {email} 已走其他线路，本路关闭");
                            Report("closed");
                            return;
                        }
                        if (IPEndPoint.TryParse(p.V4, out joinerEp))
                        {
                            peerPunchAt = p.PunchAt;
                            break;
                        }
                    }
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                if (joinerEp is null)
                {
                    LogLine($"[P2P] {email} 候选不可用（走中转）");
                    _failed[email] = DateTime.UtcNow;
                    Report("no_peer");
                    return;
                }

                // P0-E LAN 优先：对加入者的私网候选发 magic 探测（同网段即 100% 直连；AP 隔离时 1.5s 内失败回落）
                var magic = RoomMagic(_code, secret);
                if (joiner.PunchPort > 0)
                {
                    foreach (var ip in joiner.V4Lan ?? (IReadOnlyList<string>)Array.Empty<string>())
                    {
                        if (!IPAddress.TryParse(ip, out var lanIp)) continue;
                        var lanEp = new IPEndPoint(lanIp, joiner.PunchPort);
                        punchSw.Start();
                        var lanObserved = await Task.Run(() => HolePunch(udp, new[] { lanEp }, magic + "|H|",
                            magic + "|", 1500, ct), ct).ConfigureAwait(false);
                        if (lanObserved is not null)
                        {
                            LogLine($"[P2P] {email} 同网段直连成功（对端 {lanObserved}），等待游戏流量…");
                            Report("lan");
                            await ServeKcpAsync(udp, lanObserved, email, ct).ConfigureAwait(false);
                            return;
                        }
                    }
                }

                // 公网打洞（v49.4 池化引擎）：P1-F 同步起打 + 每 socket 映射/预测窗口/邻域批量齐发
                if (await WaitForRendezvousAsync(lastSrvNow, peerPunchAt, ct).ConfigureAwait(false))
                    LogLine($"[P2P] {email} 已同步起打");
                var punchTimeout = !preserving && peerSymmetric && !anyPrediction ? 7000
                    : !preserving || peerSymmetric ? 10000 : 15000;
                var targets = BuildPunchTargets(joiner, joinerEp, peerSymmetric || !preserving ? 12 : 8);
                LogLine($"[P2P] {email} 开始打洞（{pool.Count} 路并发 × 目标 {targets.Count}（含 {joinerEp}），{punchTimeout / 1000}s 窗口）…");
                punchSw.Start();
                var win = await PunchPoolAsync(pool, targets, magic + "|H|",
                    magic + "|", punchTimeout, ct).ConfigureAwait(false);
                if (win is null)
                {
                    LogLine($"[P2P] {email} 打洞未成功（走中转）");
                    _failed[email] = DateTime.UtcNow;
                    Report("timeout");
                    return;
                }
                LogLine($"[P2P] {email} 打洞成功（对端 {win.Observed}），等待游戏流量…");
                Report("udp");
                await ServeKcpAsync(win.Socket, win.Observed, email, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogLine($"[P2P] {email} 直连路异常：" + ex.Message);
                _failed[email] = DateTime.UtcNow;
                Report("error");
            }
            finally
            {
                foreach (var s in pool)
                {
                    try { s.Dispose(); } catch { }
                }
                _active.TryRemove(email, out _);
            }
        }

        /// <summary>KCP 会话：收到加入端首包（MC 客户端流量）后懒连接本地 MC 服务器并双向泵；
        /// 等待会话结束（断开/超时/取消）后返回，socket 由路线 finally 统一释放。
        /// P2-G2：会话期间每 15s 向对端发裸 magic keep-alive（保持 host 侧 NAT 映射）。</summary>
        private async Task ServeKcpAsync(Socket udp, IPEndPoint observed, string email, CancellationToken ct)
        {
            TcpClient? mc = null;
            using var kaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var kaTask = Task.Run(async () =>
            {
                var magic = Encoding.ASCII.GetBytes(MagicPrefix + "KA");
                while (!kaCts.IsCancellationRequested)
                {
                    try { udp.SendTo(magic, observed); }
                    catch { break; }
                    // 只发不收：接收循环归 KCP transport 独占；对端 KA 包由 KcpSharp 按会话 ID 静默丢弃
                    try { await Task.Delay(15000, kaCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }, CancellationToken.None);
            try
            {
                // 清掉打洞阶段的残留 magic 包，再把 socket 交给 KCP
                var junk = new byte[128];
                var any = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                while (udp.Poll(0, SelectMode.SelectRead)) udp.ReceiveFrom(junk, ref any);

                using var transport = KcpSocketTransport.CreateConversation(udp, observed, ConvId, GameKcpOptions());
                transport.Start();
                var conv = transport.Connection;
                var buf = new byte[65536];
                while (!ct.IsCancellationRequested)
                {
                    var r = await conv.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.TransportClosed) { LogLine($"[P2P] {email} 对端关闭隧道"); break; }
                    if (mc is null)
                    {
                        mc = new TcpClient();
                        await mc.ConnectAsync(IPAddress.Loopback, ResolvePort(), ct).ConfigureAwait(false);
                        LogLine($"[P2P] {email} 游戏流量接入本地 MC 服务器（端口 {ResolvePort()}）");
                        _ = PumpTcpToKcp(mc, conv, ct);
                    }
                    await mc.GetStream().WriteAsync(buf.AsMemory(0, r.BytesReceived), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogLine($"[P2P] {email} KCP 会话结束：" + ex.Message);
            }
            finally
            {
                mc?.Dispose();
                LogLine($"[P2P] {email} 隧道会话关闭");
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _udp?.Dispose(); } catch { }
            _cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- 打洞与泵（共享实现）

    /// <summary>池化 NAT 探测结果：Mapped=socket0 公网映射，Preserving=cone 判定，
    /// Samples=按发送顺序（≈NAT 分配顺序）的全部样本，Maps=每 socket 最后一个观察映射，
    /// Delta=端口递增步长（0=学不到），PredPorts=对称 NAT 的下一映射预测窗（兼容字段，cone 空）。</summary>
    private sealed record NatProbe(string Mapped, bool Preserving, List<string> Samples,
        List<IPEndPoint> Maps, List<string> MapsStrings, int Delta, List<int> PredPorts);

    private static string PortOf(string s) => s.Split(':')[^1];

    /// <summary>
    /// v49.4 池化 NAT 探测：socket 池逐个向云机 STUN-lite（8802/8803）采样（每 socket 得到
    /// 独立映射——cone 侧即 12 个对端可直接命中的稳定端口），socket0 额外探 2 个公共 STUN
    /// （RFC 5389）做端口保持性判定；全部样本按发送顺序（≈NAT 分配顺序）返回，供步长学习。
    /// 总耗时固定 ≤2.5s。判定：socket0 全部目标样本端口一致 + 其余每 socket 双目标一致 → cone；
    /// 任一 socket 对不同目标出现不同端口 → 对称（EDM）。
    /// </summary>
    private static NatProbe ProbeNatPool(IReadOnlyList<Socket> sockets, string apiHost, CancellationToken ct)
    {
        var samples = new List<string>();

        var liteTargets = new List<IPEndPoint>();
        foreach (var port in StunPorts)
        {
            try { liteTargets.Add(new IPEndPoint(IPAddress.Parse(apiHost), port)); } catch { /* 地址异常跳过 */ }
        }

        // 公共 STUN 目标（DNS 解析失败静默跳过，降级为纯云机采样）
        var stunTargets = new List<IPEndPoint>();
        var txids = new List<byte[]>();
        if (UsePublicStun)
        {
            foreach (var (host, port) in new[] { ("stun.chat.bilibili.com", 3478), ("stun.cloudflare.com", 3478) })
            {
                try
                {
                    var ip = Dns.GetHostAddresses(host)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip is null) continue;
                    stunTargets.Add(new IPEndPoint(ip, port));
                    var txid = new byte[12];
                    Random.Shared.NextBytes(txid);
                    txids.Add(txid);
                }
                catch { /* 不可达跳过 */ }
            }
        }

        var nSockets = sockets.Count;
        var baseSock = sockets[0];
        // 每 (socket, 云机目标) 的观察样本；publics 只由 socket0 探测
        var cloud = new string?[nSockets][];
        for (var i = 0; i < nSockets; i++) cloud[i] = new string?[liteTargets.Count];
        var pubs = new string?[stunTargets.Count];

        // 发送顺序 = NAT 分配顺序的学习基础：先公共 STUN（socket0），再逐 socket 云机对
        for (var i = 0; i < stunTargets.Count; i++)
        {
            var req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            Array.Copy(txids[i], 0, req, 8, 12);
            try { baseSock.SendTo(req, stunTargets[i]); } catch { }
        }
        var probeReq = Encoding.ASCII.GetBytes("MCSTUN1");
        for (var si = 0; si < nSockets; si++)
        {
            for (var ti = 0; ti < liteTargets.Count; ti++)
            {
                try { sockets[si].SendTo(probeReq, liteTargets[ti]); } catch { /* 发送失败按该目标缺席 */ }
            }
        }

        // 统一收包循环：按 (接收 socket, 来源端点) 解复用，全部样本到位或 2.5s 截止
        var expected = nSockets * liteTargets.Count + stunTargets.Count;
        var got = 0;
        var buf = new byte[512];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2500 && got < expected && !ct.IsCancellationRequested)
        {
            for (var si = 0; si < nSockets; si++)
            {
                var s = sockets[si];
                while (s.Poll(0, SelectMode.SelectRead))
                {
                    var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref from); }
                    catch (SocketException) { break; }
                    if (from is not IPEndPoint ep) continue;
                    var text = Encoding.ASCII.GetString(buf, 0, n);
                    if (text.StartsWith("MCSTUNR", StringComparison.Ordinal))
                    {
                        var ti = liteTargets.FindIndex(t => t.Equals(ep));
                        if (ti >= 0 && cloud[si][ti] is null)
                        {
                            cloud[si][ti] = text["MCSTUNR".Length..];
                            got++;
                        }
                        continue;
                    }
                    if (si == 0)
                    {
                        var idx = stunTargets.FindIndex(t => t.Equals(ep));
                        if (idx >= 0 && pubs[idx] is null && n >= 20 && buf[0] == 0x01 && buf[1] == 0x01
                            && buf[8] == txids[idx][0] && buf[9] == txids[idx][1])
                        {
                            var mappedAddr = ParseStunMapped(buf, n);
                            if (mappedAddr is not null)
                            {
                                pubs[idx] = mappedAddr;
                                got++;
                            }
                        }
                    }
                }
            }
            if (got < expected) Thread.Sleep(25);
        }

        // 顺序样本（学习分配序列）：公共（socket0）在前，随后逐 socket 云机对
        foreach (var p in pubs) if (p is not null) samples.Add(p);
        for (var si = 0; si < nSockets; si++)
            for (var ti = 0; ti < liteTargets.Count; ti++)
                if (cloud[si][ti] is not null) samples.Add(cloud[si][ti]!);

        // cone 判定：socket0 全部样本端口一致（公共+云机 ≥3 目标为确定性判定），
        // 且其余每 socket 的云机对一致；样本不足时乐观保留 cone（打洞超时兜底）
        static bool SamePorts(IEnumerable<string> ss)
        {
            var ports = ss.Select(PortOf).ToList();
            return ports.Count == 0 || ports.All(p => p == ports[0]);
        }
        var baseAll = pubs.Where(p => p is not null).Select(p => p!)
            .Concat(cloud[0].Where(p => p is not null).Select(p => p!)).ToList();
        var preserving = SamePorts(baseAll);
        for (var si = 1; si < nSockets && preserving; si++)
            preserving = SamePorts(cloud[si].Where(p => p is not null).Select(p => p!).ToList());

        // 每 socket 映射 = 其最后一个云机样本（cone=稳定端口；对称=最近分配）
        var maps = new List<IPEndPoint>();
        var mapsStr = new List<string>();
        for (var si = 0; si < nSockets; si++)
        {
            var last = cloud[si].LastOrDefault(p => p is not null);
            if (last is null) continue;
            if (IPEndPoint.TryParse(last, out var ep))
            {
                maps.Add(ep);
                mapsStr.Add(last);
            }
        }

        // 步长学习（P1-C 增强）：顺序样本端口序列的正增量众数（≤32）
        var orderedPorts = samples.Select(PortOf).Select(s => int.TryParse(s, out var p) ? p : 0)
            .Where(p => p > 0).ToList();
        var delta = 0;
        var deltas = new List<int>();
        for (var i = 1; i < orderedPorts.Count; i++)
        {
            var d = orderedPorts[i] - orderedPorts[i - 1];
            if (d > 0 && d <= 32) deltas.Add(d);
        }
        if (deltas.Count > 0) delta = deltas.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;

        // 预测窗口（兼容字段，仅对称 NAT）：末样本 + delta·k ±1（k=1..8，≤16 个）
        var predPorts = new List<int>();
        if (!preserving && delta > 0 && orderedPorts.Count > 0)
        {
            var last = orderedPorts[^1];
            for (var k = 1; k <= 8; k++)
            {
                for (var w = -1; w <= 1; w++)
                {
                    var p = last + delta * k + w;
                    if (p > 0 && p <= 65535 && !predPorts.Contains(p)) predPorts.Add(p);
                }
            }
        }

        var mapped = cloud[0].FirstOrDefault(p => p is not null) ?? pubs.FirstOrDefault(p => p is not null) ?? $"{apiHost}:0";
        P2pTrace.Log($"NAT 探测完成：样本×{samples.Count}/期望×{expected}（socket×{nSockets}，公共 STUN×{stunTargets.Count}）"
            + $"→ {(preserving ? "cone" : "symmetric")}，delta {delta}"
            + (samples.Count == 0 ? " ← 全部无应答，NAT 判定与端口预测不可信" : ""));
        return new NatProbe(mapped!, preserving, samples, maps, mapsStr, delta, predPorts);
    }

    /// <summary>v50.6.6：NAT 映射保活。国内 CGN 的 UDP 映射超时实测 15–30s（移动宽带/蜂窝普遍
    /// 30s，多层 NAT 取最短层，20260926 调研），而「探测→候选交换→rendezvous→开窗」全程
    /// 可达 15–25s——不加保活时窗口开打时映射已过期重建（端口变化），双方朝死端口互打，
    /// 是 cone×cone 成功率仅 14.8% 的最可信解释。每 2.5s 由全部池 socket 对云机 STUN 端口
    /// 重发一轮自探包（RFC 4787 REQ-6：出向包刷新映射计时），候选在窗口内保持有效。
    /// ct 取消 / 75s 兜底 / socket 释放（SendTo 异常）均自然退出，不影响打洞主流程。</summary>
    private static async Task KeepNatMappingsAsync(IReadOnlyList<Socket> sockets, string apiHost,
        CancellationToken ct)
    {
        var req = Encoding.ASCII.GetBytes("MCSTUN1");
        var targets = new List<IPEndPoint>();
        try
        {
            foreach (var port in StunPorts)
                targets.Add(new IPEndPoint(IPAddress.Parse(apiHost), port));
        }
        catch { return; }
        var deadline = Environment.TickCount64 + 75_000;
        try
        {
            while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                await Task.Delay(2500, ct).ConfigureAwait(false);
                foreach (var s in sockets)
                {
                    if (s is null) continue;
                    foreach (var t in targets)
                    {
                        try { s.SendTo(req, t); }
                        catch (ObjectDisposedException) { return; }
                        catch (SocketException) { }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 保活异常不影响打洞主流程 */ }
    }

    /// <summary>解析 STUN Binding Success 中的 XOR-MAPPED-ADDRESS(0x0020) 或 MAPPED-ADDRESS(0x0001)。</summary>
    private static string? ParseStunMapped(byte[] buf, int n)
    {
        int off = 20;
        while (off + 4 <= n)
        {
            int type = (buf[off] << 8) | buf[off + 1];
            int len = (buf[off + 2] << 8) | buf[off + 3];
            if (type is 0x0020 or 0x0001 && len >= 8 && off + 4 + len <= n)
            {
                int p = off + 4;
                int rawPort = (buf[p + 2] << 8) | buf[p + 3];
                var addrBytes = new byte[4];
                Array.Copy(buf, p + 4, addrBytes, 0, 4);
                if (type == 0x0020)
                {
                    rawPort ^= 0x2112; // magic cookie 高 16 位参与端口异或
                    addrBytes[0] ^= 0x21; addrBytes[1] ^= 0x12;
                    addrBytes[2] ^= 0xA4; addrBytes[3] ^= 0x42;
                }
                return $"{new IPAddress(addrBytes)}:{rawPort}";
            }
            off += 4 + ((len + 3) & ~3);
        }
        return null;
    }

    /// <summary>
    /// P1-F 同步起打：按服务器锚定的 punch_at（服务器时钟）等待到同一绝对时刻。
    /// 时钟校准：offset = 本机时钟 − 服务器时钟（取自同一响应的 srv_now，精度 ±RTT/2 足够）。
    /// 等待上限 5s：偏差过大或锚点已过期时放弃同步，退化为原来的自然重叠（双方长窗口兜底）。
    /// 返回是否执行了同步等待。
    /// </summary>
    private static async Task<bool> WaitForRendezvousAsync(double srvNow, double punchAt, CancellationToken ct)
    {
        if (srvNow <= 0 || punchAt <= 0) return false;
        var nowLocal = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var offset = nowLocal - srvNow;
        var waitMs = (int)((punchAt + offset - nowLocal) * 1000);
        if (waitMs <= 0 || waitMs > 5000) return false;
        await Task.Delay(waitMs, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 双向打洞：持续向目标组发 magic 并监听；收到对端包即认为洞已开，返回观察到的对端真实地址。
    /// P1-C：对端为对称 NAT 时 targets 含其预测端口窗口（每个窗口轮发包，任何一路命中即成功）。
    /// </summary>
    private static IPEndPoint? HolePunch(Socket udp, IReadOnlyList<IPEndPoint> targets, string magic,
        string validPrefix, int timeoutMs, CancellationToken ct)
    {
        var payloadRaw = Encoding.ASCII.GetBytes(magic + Guid.NewGuid().ToString("N")[..8]);
        // v49：补齐到 64B——部分 CGNAT/防火墙对超小 UDP 包做 QoS 丢弃
        var payload = payloadRaw.Length >= 64 ? payloadRaw : PadTo(payloadRaw, 64);
        var echo = Encoding.ASCII.GetBytes(magic.Replace("|J|", "|E|").Replace("|H|", "|E|"));
        var buf = new byte[128];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            foreach (var t in targets)
            {
                try { udp.SendTo(payload, t); } catch (SocketException) { /* 单目标失败不致命，继续 */ }
            }
            // v49：100ms 轮询（原 300ms）——同窗口发包量 ×3，窗口内命中概率提升
            if (udp.Poll(100_000, SelectMode.SelectRead))
            {
                var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = udp.ReceiveFrom(buf, ref from); }
                catch (SocketException) { continue; }
                var text = Encoding.ASCII.GetString(buf, 0, n);
                if (text.StartsWith(validPrefix, StringComparison.Ordinal))
                {
                    if (!text.Contains("|E|", StringComparison.Ordinal))
                    {
                        try { udp.SendTo(echo, from); } catch { /* 回声失败不致命 */ }
                    }
                    return (IPEndPoint)from;
                }
            }
        }
        return null;
    }

    /// <summary>v49.4 打洞目标组：对端每 socket 映射（cone=稳定直达，最优先）→ 外部映射（UPnP/PMP/PCP）
    /// → 主映射 → 对端自报预测窗 → 由对端 delta 推导的扩展窗（k=1..20 ±2，覆盖对端起打后的
    /// 新分配；打洞首轮回发包即按此序列分配）→ 主映射邻域 ±spread（CGNAT 半对称主因）。
    /// 全部限定对端公网 IP，按端口去重，封顶 72 个。</summary>
    private static List<IPEndPoint> BuildPunchTargets(P2pPeer peer, IPEndPoint primary, int spread)
    {
        var list = new List<IPEndPoint>();
        var seen = new HashSet<int>();
        void Add(IPEndPoint ep)
        {
            if (list.Count >= 72) return;
            if (ep.Port is > 0 and < 65536 && seen.Add(ep.Port)) list.Add(ep);
        }

        foreach (var m in peer.Maps ?? (IReadOnlyList<string>)Array.Empty<string>())
        {
            if (IPEndPoint.TryParse(m, out var ep) && ep.Address.Equals(primary.Address))
                Add(ep);
        }
        if (peer.UpnpPort > 0) Add(new IPEndPoint(primary.Address, peer.UpnpPort));
        Add(primary);
        foreach (var p in peer.PredPorts ?? (IReadOnlyList<int>)Array.Empty<int>())
            Add(new IPEndPoint(primary.Address, p));
        // 扩展窗：对端最新映射 + delta·k（k=1..20）±2——对端打洞首轮回的每个新目标端口
        // 都会按分配序列落在此区间内，扫描即可命中其任意 socket 的早期映射
        if (peer.Delta > 0 && (peer.Maps?.Count ?? 0) > 0
            && IPEndPoint.TryParse(peer.Maps![^1], out var lastMap))
        {
            for (var k = 1; k <= 20; k++)
            {
                for (var w = -2; w <= 2; w++)
                {
                    var p = lastMap.Port + peer.Delta * k + w;
                    if (p > 0 && p < 65536) Add(new IPEndPoint(primary.Address, p));
                }
            }
        }
        // 邻域扫描：主映射 ±spread——实测 CGNAT「半对称」：对多数目标复用映射端口，
        // 对新目标（对端）分配邻近端口；两侧均判 cone 却打洞超时的主因
        if (spread > 0)
        {
            for (var d = 1; d <= spread; d++)
            {
                Add(new IPEndPoint(primary.Address, primary.Port - d));
                Add(new IPEndPoint(primary.Address, primary.Port + d));
            }
        }
        return list;
    }

    private static byte[] PadTo(byte[] src, int len)
    {
        var dst = new byte[len];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    /// <summary>释放池中除 keep 外的全部 socket（keep 交由 KcpUpstream/调用方接管）。</summary>
    private static void DisposeExcept(IEnumerable<Socket> pool, Socket? keep)
    {
        foreach (var s in pool)
        {
            if (ReferenceEquals(s, keep)) continue;
            try { s.Dispose(); } catch { }
        }
    }

    /// <summary>池化打洞胜者：命中的本地 socket + 观察到的对端真实地址。</summary>
    private sealed record PunchWin(Socket Socket, IPEndPoint Observed);

    /// <summary>
    /// v49.4 池化打洞引擎：socket 池全部并发向目标组发 magic 并监听，任一 socket 先收到
    /// 对端包即胜（立即返回，不再像 v49 双 socket 那样等两路全部跑完）。
    /// 发包节奏：前 3 轮每 70ms 全量突发（起打对齐窗口内的首包命中率），其后每 180ms
    /// 每 socket 轮转 16 目标子集（限制稳态 pps，防 CGNAT 泛洪检测）；55% 窗口后加入
    /// ±160 随机彩票口（随机型对称 NAT 的 birthday 补救）。收到合法 magic 即回 echo
    /// （对端同刻确认），返回胜者。旧版 HolePunch 保留给 LAN 直连用。
    /// </summary>
    private static async Task<PunchWin?> PunchPoolAsync(
        IReadOnlyList<Socket> sockets, IReadOnlyList<IPEndPoint> targets, string magic,
        string validPrefix, int timeoutMs, CancellationToken ct)
    {
        return await Task.Run(() => PunchPoolCore(sockets, targets, magic, validPrefix, timeoutMs, ct), ct)
            .ConfigureAwait(false);
    }

    private static PunchWin? PunchPoolCore(
        IReadOnlyList<Socket> sockets, IReadOnlyList<IPEndPoint> targets, string magic,
        string validPrefix, int timeoutMs, CancellationToken ct)
    {
        if (targets.Count == 0) return null;
        var payloadRaw = Encoding.ASCII.GetBytes(magic + Guid.NewGuid().ToString("N")[..8]);
        // v49：补齐到 64B——部分 CGNAT/防火墙对超小 UDP 包做 QoS 丢弃
        var payload = payloadRaw.Length >= 64 ? payloadRaw : PadTo(payloadRaw, 64);
        var echo = Encoding.ASCII.GetBytes(magic.Replace("|J|", "|E|").Replace("|H|", "|E|"));
        var buf = new byte[160];
        var address = targets[0].Address;
        var basePort = targets[0].Port;
        var rnd = new Random();
        var lottery = new List<IPEndPoint>();
        var lotteryAt = (long)(timeoutMs * 0.55);
        var chunks = Math.Max(1, (targets.Count + 15) / 16);
        var sw = Stopwatch.StartNew();
        var round = 0;
        while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            // 迟段随机扩窗：对端若为随机分配型对称 NAT，唯一机会是撞端口
            if (lottery.Count == 0 && sw.ElapsedMilliseconds >= lotteryAt)
            {
                for (var i = 0; i < 10; i++)
                {
                    var p = basePort + rnd.Next(-160, 161);
                    if (p is > 1024 and < 65535 && targets.All(t => t.Port != p))
                        lottery.Add(new IPEndPoint(address, p));
                }
                if (lottery.Count > 0)
                    P2pTrace.Log($"打洞加入随机扩窗 ×{lottery.Count}（±160 彩票口，第 {round} 轮起）");
            }
            var count = targets.Count + lottery.Count;
            for (var si = 0; si < sockets.Count; si++)
            {
                var s = sockets[si];
                if (round < 3)
                {
                    foreach (var t in targets) SendTo(s, payload, t);
                    foreach (var t in lottery) SendTo(s, payload, t);
                }
                else
                {
                    // 每 socket 轮转 16 目标分片：稳态 pps = sockets×16/180ms
                    var chunk = (round + si) % chunks;
                    var from = chunk * 16;
                    var to = Math.Min(count, from + 16);
                    for (var j = from; j < to; j++)
                        SendTo(s, payload, j < targets.Count ? targets[j] : lottery[j - targets.Count]);
                }
            }
            round++;
            var roundMs = round <= 3 ? 70 : 180;
            var phaseEnd = sw.ElapsedMilliseconds + roundMs;
            while (sw.ElapsedMilliseconds < phaseEnd && sw.ElapsedMilliseconds < timeoutMs
                   && !ct.IsCancellationRequested)
            {
                for (var si = 0; si < sockets.Count; si++)
                {
                    var s = sockets[si];
                    while (s.Poll(0, SelectMode.SelectRead))
                    {
                        var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
                        int n;
                        try { n = s.ReceiveFrom(buf, ref from); }
                        catch (SocketException) { break; }
                        var text = Encoding.ASCII.GetString(buf, 0, n);
                        if (!text.StartsWith(validPrefix, StringComparison.Ordinal)) continue;
                        if (!text.Contains("|E|", StringComparison.Ordinal))
                        {
                            try { s.SendTo(echo, from); } catch { /* 回声失败不致命 */ }
                        }
                        return new PunchWin(s, (IPEndPoint)from);
                    }
                }
                if (sw.ElapsedMilliseconds < phaseEnd - 8) Thread.Sleep(8);
            }
        }
        return null;

        static void SendTo(Socket s, byte[] payload, IPEndPoint t)
        {
            try { s.SendTo(payload, t); } catch (SocketException) { /* 单目标失败不致命 */ }
        }
    }

    /// <summary>IPv6 直连测试：逐个候选 TCP 连接（5s 超时），成功返回已连接的 TcpClient。
    /// v46.2：超时/失败只记日志并试下一候选——原实现超时的 OperationCanceledException 会
    /// 外溢中断整个加入流程（连中转回退都到不了），顺手修复。</summary>
    private static async Task<TcpClient?> TryV6DirectAsync(IReadOnlyList<string> v6List, int port, CancellationToken ct)
    {
        foreach (var v6 in v6List)
        {
            if (!IPAddress.TryParse(v6, out var ip))
            {
                P2pTrace.Log($"v6 候选无效已跳过：{v6}");
                continue;
            }
            try
            {
                var client = new TcpClient(AddressFamily.InterNetworkV6);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(5000);
                await client.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
                P2pTrace.Log($"v6 候选连接成功：[{v6}]:{port}");
                return client;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                P2pTrace.Log($"v6 候选超时（5s 无响应，可能被防火墙拦截或非全球地址）：[{v6}]:{port}");
            }
            catch (Exception e)
            {
                P2pTrace.Log($"v6 候选连接失败：[{v6}]:{port} · {e.GetType().Name}: {e.Message}");
            }
        }
        return null;
    }

    private static async Task PumpTcpToKcp(TcpClient mc, KcpConversation conv, CancellationToken ct)
    {
        var buf = new byte[65536];
        var stream = mc.GetStream();
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(buf, ct).ConfigureAwait(false); }
            catch { break; }
            if (n == 0) break;
            try { if (!await conv.SendAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false)) break; }
            catch { break; }
        }
    }

    private static async Task PumpKcpToTcp(KcpConversation conv, TcpClient mc, CancellationToken ct)
    {
        var buf = new byte[65536];
        var stream = mc.GetStream();
        while (!ct.IsCancellationRequested)
        {
            KcpConversationReceiveResult r;
            try { r = await conv.ReceiveAsync(buf, ct).ConfigureAwait(false); }
            catch { break; }
            if (r.TransportClosed) break;
            // MC 断开后流被释放属正常收尾；异常逃逸会变成未观测任务异常弹窗（好友端实测踩坑）
            try { await stream.WriteAsync(buf.AsMemory(0, r.BytesReceived), ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    private static async Task CopyStreamToStream(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buf = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await from.ReadAsync(buf, ct).ConfigureAwait(false); }
            catch { break; }
            if (n == 0) break;
            await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
        }
    }

    /// <summary>收集本机 Up 网卡的私网 IPv4（10 / 172.16-31 / 192.168 段，排除回环），最多 4 个。
    /// 用途：同网段好友直连候选（P0-E）；AP 隔离环境下探测失败自然回落公网打洞。</summary>
    private static List<string> CollectV4Lan()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ip.GetAddressBytes();
                    var isLan = b.Length == 4 && (
                        b[0] == 10 ||
                        (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                        (b[0] == 192 && b[1] == 168));
                    if (isLan) list.Add(ip.ToString());
                }
            }
        }
        catch { /* 网卡枚举失败按无 LAN 处理 */ }
        return list.Distinct().Take(4).ToList();
    }

    /// <summary>收集本机全局 IPv6 地址（排除链路本地/站点本地/ULA/多播），最多 4 个。</summary>
    private static List<string> CollectV6()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = a.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Teredo || ip.IsIPv6UniqueLocal) continue;
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length != 16 || bytes[0] == 0xfc || bytes[0] == 0xfd || bytes[0] == 0xff) continue;
                    list.Add(ip.ToString());
                }
            }
        }
        catch { /* 网卡枚举失败按无 v6 处理 */ }
        return list.Distinct().Take(4).ToList();
    }

    /// <summary>房主侧防火墙放行（java.exe 入站，供 IPv6 直连与打洞回包；非管理员时静默失败）。</summary>
    public static void EnsureFirewallRule(string javaExe)
    {
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"MCStudio P2P\"");
            RunNetsh($"advfirewall firewall add rule name=\"MCStudio P2P\" dir=in action=allow program=\"{javaExe}\" enable=yes profile=any");
            // v49：打洞收包方是启动器进程自身（punch socket），PCL exe 也要 UDP 入站放行——
            // 多 exe 部署下旧规则绑的是别的 exe 路径，按名重建绑定当前进程
            var self = Environment.ProcessPath ?? "";
            if (self.Length > 0)
            {
                RunNetsh($"advfirewall firewall delete rule name=\"MCStudio P2P Self\"");
                RunNetsh($"advfirewall firewall add rule name=\"MCStudio P2P Self\" dir=in action=allow " +
                         $"program=\"{self}\" protocol=udp enable=yes profile=any");
            }
        }
        catch { /* 非管理员：回退链兜底 */ }
    }

    private static void RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        p?.WaitForExit(5000);
    }
}
